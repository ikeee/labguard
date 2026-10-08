using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 断网遮罩（"屏保式"）：
    /// · 全屏置顶、隐藏光标、吞掉所有键（含 Win/Alt+Tab/Ctrl+Esc）
    /// · **插回网线后自动消失**（不需要老师，也不消耗任何权限）
    /// · 老师出口：连续按 5 次 Esc 弹出密码框 → 输入正确密码则解除并暂停监控（防"整排机器卡死没人能救"）
    /// · 背景：默认纯深色（Plain）；也可选安装目录 wallpaper 里随机一张。
    ///   **两种都只画在遮罩窗口内部，本程序从不修改学生机的系统壁纸。**
    /// </summary>
    public class DisconnectMaskForm : Form
    {
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Label _title;
        private readonly Label _hint;
        private readonly Label _number;
        private string _machineNumber = "";
        private readonly Panel _card;
        private Image _background;
        private Func<bool> _networkRestored;
        private Func<string> _elapsedText;
        private Action<string> _teacherUnlock;   // 参数是解除通道："手势" / "密码"
        private string _passwordHash;
        private IntPtr _hook = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;
        private int _escCount;
        private DateTime _escFirst = DateTime.MinValue;
        private bool _closed;

        /// <summary>后台网络轮询得到的"网线插回来了"标志，只由 <see cref="NetWatchBody"/> 置位。</summary>
        private volatile bool _netRestored;

        /// <summary>
        /// 代际号：每次启动/停止监听都 +1。后台线程只在自己这一代仍有效时才写 _netRestored，
        /// 避免上一次卡死的线程苏醒后误判（网卡枚举在媒体断开时可能永久挂起）。
        /// </summary>
        private volatile int _netWatchGen;

        /// <summary>诊断用：Tick 计数。</summary>
        private int _tickCount;

        /// <summary>
        /// 老师的密码框正在开着。此时全局键盘钩子必须<b>放行所有按键</b>，
        /// 否则老师敲的字母数字会被我们自己吞掉 —— 表现为"密码框看得见却打不出字"。
        /// 钩子不能只靠 InputLock 解锁来解决：那是另一套机制（BlockInput），管不到钩子。
        /// </summary>
        private volatile bool _teacherDialogOpen;

        /// <summary>
        /// 构造此窗体的线程（= UI 线程）的托管线程 ID。
        ///
        /// 为什么必须记它：<c>Control.InvokeRequired</c> 在**句柄尚未创建**时会返回 false
        /// （源码里 <c>if (IsHandleCreated) {...} return false;</c>）。而本窗体是被
        /// <c>GuardBase</c> 的 <c>System.Timers.Timer</c>（线程池线程）第一次调用的，
        /// 于是"看起来不需要跨线程"，<c>Show()</c> 就把窗口建到了线程池线程上。
        /// 那个线程没有消息循环 → WM_TIMER / BeginInvoke 全部不执行 → 遮罩永不自动消失，
        /// 且心跳断供导致 BlockInput 看门狗 10 秒后自动解锁。用线程 ID 判断才可靠。
        /// </summary>
        private readonly int _uiThreadId;

        private const int WhKeyboardLl = 13;

        /// <summary>诊断/自检用：全局键盘钩子当前是否已安装（解除后应为 false）。</summary>
        public bool IsHookInstalled { get { return _hook != IntPtr.Zero; } }

        /// <summary>遮罩期间是否硬锁鼠标键盘（InputLock.ModeOff / ModeOn），由 Agent 从配置注入。</summary>
        public string InputLockMode { get; set; } = InputLock.ModeOn;

        private bool _inputEngaged;

        // ---------------- 老师解锁通道之二：按键手势（Pause → ↑↑↓↓←→←→） ----------------
        // 全部由 Agent 从配置注入（与 InputLockMode 同款风格，避免 ShowMask 参数继续膨胀）。
        /// <summary>Gesture = 只要手势；Password = 只要 5×Esc + 密码；Both = 两条都可用（默认）。</summary>
        public string TeacherUnlockMode { get; set; } = "Both";
        /// <summary>手势激活键：Pause（默认）/ ScrollLock。**只能是单键**。</summary>
        public string GestureArmKey { get; set; } = "Pause";
        /// <summary>解锁序列文本，逗号分隔，只认 U/D/L/R。默认 ↑↑↓↓←→←→。</summary>
        public string GestureSequence { get; set; } = "U,U,D,D,L,R,L,R";
        /// <summary>激活后必须在此秒数内输完，超时自动重置。</summary>
        public int GestureWindowSeconds { get; set; } = 5;
        /// <summary>是否显示进度点。**学生也看得见**，默认关。</summary>
        public bool GestureFeedback { get; set; }

        private UnlockGesture _gesture;
        private DateTime _gestureHintUntil = DateTime.MinValue;

        /// <summary>手势通道是否启用。</summary>
        private bool GestureOn { get { return TeacherUnlockMode != "Password"; } }
        /// <summary>密码通道（5×Esc + 密码框）是否启用。</summary>
        private bool PasswordOn { get { return TeacherUnlockMode != "Gesture"; } }

        public DisconnectMaskForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;   // 多屏：连非主显示器一起盖住
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(8, 14, 28);
            Font = new Font("微软雅黑", 12F);
            DoubleBuffered = true;

            _card = new Panel
            {
                Size = new Size(900, 320),
                BackColor = Color.FromArgb(200, 0, 0, 0)
            };
            _title = new Label
            {
                Dock = DockStyle.Top,
                Height = 110,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(255, 214, 102),
                Font = new Font("微软雅黑", 40F, FontStyle.Bold)
            };
            _hint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.WhiteSmoke,
                Font = new Font("微软雅黑", 17F)
            };
            _number = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(180, 230, 230, 230),
                Font = new Font("微软雅黑", 15F, FontStyle.Bold)
            };
            _card.Controls.Add(_hint);
            _card.Controls.Add(_number);
            _card.Controls.Add(_title);
            Controls.Add(_card);

            _tick.Tick += (s, e) =>
            {
                try
                {
                    // 兜底第一句：遮罩已经解除（_closed）就立刻停表并**停止续心跳**。
                    // 只要不再续心跳，InputLock 的 10 秒看门狗一定会把输入放开 ——
                    // 这是"鼠标键盘锁死"这类事故的最后一道保险（见 AskTeacherPassword 的历史坑）。
                    if (_closed) { _tick.Stop(); return; }
                    // 心跳第一句就供上：任何后续异常都不再让硬锁被误判为“界面已无响应”
                    InputLock.KeepAlive();
                    _tickCount++;
                    if (_tickCount % 5 == 1) Log.Info("[遮罩心跳] #" + _tickCount + " netRestored=" + _netRestored);
                    if (_netRestored) { AutoClose("网络已恢复"); return; }
                    UpdateBottomLine();
                }
                catch (Exception ex) { Log.Warn("[遮罩心跳] Tick 异常：" + ex.Message); }
            };

            // 关键：在构造线程（UI 线程）上就把窗体句柄建出来。
            // 否则首次由 Guard 的线程池线程调用 ShowMask 时，窗口会被建到那个没有消息循环的线程上，
            // 遮罩就再也不会自动消失了（详见 _uiThreadId 的说明）。
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                _ = Handle;                              // 触发 CreateHandle()，句柄归属本线程
                Log.Info("[遮罩] 窗体已就绪，UI 线程 " + _uiThreadId);
            }
            catch (Exception ex)
            {
                // 没有桌面/窗口站时（例如被误在会话 0 启动）建句柄会失败；
                // 这里只记日志，不让构造函数抛出去把整个小助手带崩。
                Log.Warn("[遮罩] 预先创建窗体句柄失败（可能无桌面会话）：" + ex.Message);
            }
        }

        /// <summary>底部那一行的实际文本（截图/自检用）。</summary>
        public string BottomLineText { get { return _number.Text; } }

        /// <summary>
        /// 底部那一行：机位号 + 已断开时长。两者都要能看见，所以拼成一行，
        /// 而不是让"时长"把"机位号"顶掉（否则开了时长就永远看不到机位号）。
        /// </summary>
        private void UpdateBottomLine()
        {
            string text = _machineNumber ?? "";
            if (_elapsedText != null)
            {
                string elapsed = _elapsedText();
                if (!string.IsNullOrEmpty(elapsed))
                    text = string.IsNullOrEmpty(text) ? elapsed : text + " · " + elapsed;
            }
            // 手势反馈：进度点（只在开了 UnlockGestureFeedback 时显示，因为学生也看得见）
            if (GestureFeedback && _gesture != null && _gesture.Armed)
                text += "   " + ProgressDots();
            // 失败/超时的极轻提示：老师按错了要能知道，否则会以为机器坏了
            if (_gestureHintUntil > DateTime.Now)
                text += "   手势未识别";
            _number.Text = text;
        }

        /// <summary>提示老师怎么解除——按当前启用的通道给出对应文案。</summary>
        private string TeacherTip()
        {
            const string auto = "恢复网络后 10 秒内自动消失";
            string gesture = "老师：按 Pause 后按 ↑↑↓↓←→←→ 解除";
            if (GestureArmKey == "ScrollLock") gesture = "老师：按 Scroll Lock 后按 ↑↑↓↓←→←→ 解除";
            if (GestureOn && PasswordOn) return "（" + auto + "；" + gesture + "；或连按 5 次 Esc 输密码）";
            if (GestureOn) return "（" + auto + "；" + gesture + "）";
            return "（" + auto + "；老师连续按 5 次 Esc 可输入密码解除）";
        }

        /// <summary>进度点：已匹配的画 ●，未匹配的画 ○。</summary>
        private string ProgressDots()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _gesture.Sequence.Length; i++) sb.Append(i < _gesture.Position ? "●" : "○");
            return sb.ToString();
        }

        public void ShowMask(string machineNumber, string headLine, string advice, string passwordHash,
            bool randomWallpaper, bool showElapsed, Func<string> elapsedText, Func<bool> networkRestored,
            Action<string> teacherUnlock)
        {
            // 不能用 InvokeRequired：句柄未创建时它返回 false（见 _uiThreadId）。按线程 ID 判断才可靠。
            if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                try
                {
                    BeginInvoke(new Action(() => ShowMask(machineNumber, headLine, advice, passwordHash,
                        randomWallpaper, showElapsed, elapsedText, networkRestored, teacherUnlock)));
                }
                catch (Exception ex) { Log.Warn("[遮罩] 跨线程弹出失败：" + ex.Message); }
                return;
            }
            Log.Info("[遮罩] 在 UI 线程弹出（线程 " + _uiThreadId + "）");
            _closed = false;
            _escCount = 0;
            _passwordHash = passwordHash;
            _elapsedText = showElapsed ? elapsedText : null;
            _networkRestored = networkRestored;
            _teacherUnlock = teacherUnlock;

            _gesture = new UnlockGesture(GestureArmKey, GestureSequence, GestureWindowSeconds);
            _gestureHintUntil = DateTime.MinValue;

            _title.Text = headLine;
            _hint.Text = advice + Environment.NewLine + Environment.NewLine + TeacherTip();
            _machineNumber = machineNumber;
            UpdateBottomLine();

            LoadBackground(randomWallpaper);
            RelayoutCard();

            Bounds = SystemInformation.VirtualScreen;
            if (!_inputEngaged) { InputLock.Engage(InputLockMode); _inputEngaged = true; }
            if (!Visible) Show();
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            CursorLock.Hide();
            _tick.Start();
            InstallHook();
            StartNetWatch();
        }

        // ------------------------------------------------------------------ 网络恢复检测（后台线程）
        /// <summary>
        /// 启动后台轮询线程来判定"网线插回来了没有"。
        ///
        /// 为什么离开 UI 线程：<c>NetworkInterface.GetAllNetworkInterfaces()</c> 与
        /// <c>GetIPProperties()</c> 在**网卡被禁用 / 网线被拔掉**时可能长时间阻塞
        /// （NDIS 在媒体断开状态下查询会等待）。放在 UI 线程就会连带消息泵一起冻住：
        /// 心跳断供（BlockInput 看门狗 10 秒后自动解锁）、遮罩底部计时也停摆。
        /// 放到后台线程后，最坏情况只是"检测慢"，界面与老师解锁通道始终可用。
        ///
        /// 注意：本线程只负责**判断**，真正的收尾一律 <c>BeginInvoke</c> 回 UI 线程执行
        /// （见 <see cref="NetWatchBody"/> 与 <see cref="AutoClose"/>）。
        /// </summary>
        private void StartNetWatch()
        {
            _netRestored = false;
            int gen = unchecked(_netWatchGen + 1);
            _netWatchGen = gen;
            var t = new Thread(() => NetWatchBody(gen))
            {
                IsBackground = true,
                Name = "LabGuard-MaskNetWatch"
            };
            t.Start();
            Log.Info("[遮罩] 已启动网络恢复检测线程 gen=" + gen);
        }

        private void NetWatchBody(int gen)
        {
            int rounds = 0;
            while (gen == _netWatchGen)
            {
                rounds++;
                bool restored = false;
                bool timedOut = false;
                try
                {
                    // 查询放到线程池并限时等待：即使某次网卡查询永久挂起，也只丢掉那一条，
                    // 监听继续下一轮，不会连"插回网线也不消失"都发生。
                    var task = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { return _networkRestored != null && _networkRestored(); }
                        catch { return false; }
                    });
                    if (task.Wait(TimeSpan.FromSeconds(3))) restored = task.Result;
                    else timedOut = true;
                }
                catch { }

                if (gen != _netWatchGen) return;      // 已被停止/重启，本次结果作废
                if (restored)
                {
                    Log.Info("[遮罩] 检测到网络已恢复（第 " + rounds + " 轮）");
                    _netRestored = true;
                    // 不等 1 秒定时器：直接把这颗"该收工了"的信号交给 UI 线程，
                    // 自动消失不再依赖 WM_TIMER（那是低优先级、可被饿死的合成消息）。
                    try { BeginInvoke(new Action(() => AutoClose("网络已恢复"))); }
                    catch (Exception ex) { Log.Warn("[遮罩] 自动解除投递失败：" + ex.Message); }
                    return;
                }
                if (rounds % 5 == 0 || timedOut) Log.Info("[遮罩] 网络检测第 " + rounds + " 轮：未恢复 timedOut=" + timedOut);

                // 查询卡住时放慢重试（2 秒），正常时 1 秒一轮，别把线程池占满
                int waitMs = timedOut ? 2000 : 1000;
                for (int i = 0; i < waitMs / 100 && gen == _netWatchGen; i++) Thread.Sleep(100);
            }
        }

        private void StopNetWatch()
        {
            _netWatchGen++;                            // 令当前这一代失效
        }

        private void LoadBackground(bool randomWallpaper)
        {
            try
            {
                if (_background != null) { _background.Dispose(); _background = null; }
                if (!randomWallpaper) return;
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wallpaper");
                if (!Directory.Exists(dir)) return;
                var files = new List<string>(Directory.GetFiles(dir, "*.jpg"));
                if (files.Count == 0) return;
                _background = Image.FromFile(files[new Random().Next(files.Count)]);
                BackColor = Color.Black;
            }
            catch (Exception ex) { Log.Debug("加载遮罩背景失败：" + ex.Message); }
        }

        private void RelayoutCard()
        {
            // 卡片贴在主显示器上居中（窗口本身覆盖所有显示器）
            Rectangle pa = Screen.PrimaryScreen.Bounds, va = SystemInformation.VirtualScreen;
            _card.Left = (pa.Left - va.Left) + (pa.Width - _card.Width) / 2;
            _card.Top = (pa.Top - va.Top) + (pa.Height - _card.Height) / 2;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_background == null)
            {
                using (var brush = new LinearGradientBrush(ClientRectangle,
                           Color.FromArgb(8, 14, 28), Color.FromArgb(24, 42, 70), 35f))
                {
                    e.Graphics.FillRectangle(brush, ClientRectangle);
                }
                return;
            }
            e.Graphics.DrawImage(_background, ClientRectangle);
            using (var overlay = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                e.Graphics.FillRectangle(overlay, ClientRectangle);
            }
        }

        private void InstallHook()
        {
            if (_hook != IntPtr.Zero) return;
            try
            {
                _proc = (nCode, wParam, lParam) =>
                {
                    if (nCode >= 0)
                    {
                        // 老师在输密码 → 一字不放地交给系统（否则密码根本打不进去，见 _teacherDialogOpen）
                        if (_teacherDialogOpen)
                            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

                        int msg = wParam.ToInt32();
                        bool keyDown = (msg == 0x0100 || msg == 0x0104);
                        if (keyDown || msg == 0x0101 || msg == 0x0105)
                        {
                            int vk = Marshal.ReadInt32(lParam) & 0xFF;
                            if (!keyDown)
                            {
                                // 抬起：清掉"按住"标记，同一个键才能再次计数。
                                // 长按会连发 keydown 而中间一个 keyup 都没有（实测如此），
                                // 不去重的话老师手一抖多按半秒，序列就被冲乱、再也解锁不了。
                                if (GestureOn && _gesture != null) _gesture.KeyUp(vk);
                                return (IntPtr)1;
                            }
                            OnTeacherKeyDown(vk);
                            return (IntPtr)1;   // 按下：一律吞掉，不进系统
                        }
                    }
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                };
                _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc, NativeMethods.GetModuleHandle(null), 0);
            }
            catch (Exception ex) { Log.Warn("遮罩键盘钩子安装失败：" + ex.Message); }
        }

        private void TrackEsc()
        {
            if ((DateTime.Now - _escFirst).TotalSeconds > 3) { _escCount = 0; _escFirst = DateTime.Now; }
            _escCount++;
            if (_escCount < 5) return;
            _escCount = 0;

            // 关键：**不能在钩子回调里直接弹对话框**。
            //
            // 低级键盘钩子的调用是串行的：回调不返回，钩子链就一直被占着。
            // 若在回调里 ShowDialog()，那个模态循环会一直等到老师关掉窗口才回 ——
            // 于是期间所有按键都被系统排队，**一个都送不进输入框**，
            // 表现为"密码框出来了却打不出字"（实测：连回车都到不了，日志里根本查不到提交记录）。
            //
            // 投递回 UI 线程执行，让回调立刻返回，钩子链随即恢复。
            try { BeginInvoke(new Action(AskTeacherPassword)); }
            catch (Exception beginEx) { Log.Warn("[遮罩] 老师解锁投递失败：" + beginEx.Message); }
        }

        private void TrackEscReset() { _escCount = 0; }

        /// <summary>
        /// 老师按下一个键（此时遮罩仍在硬锁 + 全吞键状态）。分流到两条解锁通道。
        /// </summary>
        private void OnTeacherKeyDown(int vk)
        {
            try
            {
                // ① 手势通道：**全程不需要放开硬锁、不需要任何输入框**。
                //    实测证明 BlockInput(TRUE) 期间低级键盘钩子照常工作（见 docs/06），
                //    所以这条通道根本不碰 InputLock / 钩子放行 / 窗口层级 —— 那正是近三轮 Bug 的共同根源。
                if (GestureOn && _gesture != null)
                {
                    GestureResult r = _gesture.KeyDown(vk, DateTime.Now);
                    // 只记录"激活期间 + 有变化"的按键，避免学生乱按把日志刷爆
                    if (r != GestureResult.None || _gesture.Armed)
                        Log.Info("[手势] vk=0x" + vk.ToString("X2") + " → " + r + " pos=" + _gesture.Position);
                    if (r == GestureResult.Unlocked)
                    {
                        // 仍然要投递回 UI 线程：钩子回调必须立刻返回，绝不能在回调里动窗口/弹对话框。
                        // （回调不返回 → 钩子链串行阻塞 → 期间所有按键被系统排队。）
                        try { BeginInvoke(new Action(TeacherGestureUnlock)); }
                        catch (Exception ex) { Log.Warn("[遮罩] 手势解锁投递失败：" + ex.Message); }
                        return;
                    }
                    if (r == GestureResult.Mistake || r == GestureResult.Timeout)
                        _gestureHintUntil = DateTime.Now.AddSeconds(2);   // 按错要给一点反馈，否则老师以为机器坏了
                    if (r != GestureResult.None) UpdateBottomLine();
                }

                // ② 密码通道（连按 5 次 Esc → 密码框），仅在未设为纯手势时启用
                if (PasswordOn)
                {
                    if (vk == 0x1B) { TrackEsc(); return; }   // Esc
                    TrackEscReset();
                }
            }
            catch (Exception ex) { Log.Warn("[遮罩] 老师按键处理异常：" + ex.Message); }
        }

        /// <summary>手势命中 → 解除遮罩（已在 UI 线程）。</summary>
        private void TeacherGestureUnlock()
        {
            if (_closed) return;
            Log.Warn("老师用按键手势解除了断网遮罩");
            AutoClose("老师已用按键手势解除");
            try { _teacherUnlock?.Invoke("手势"); } catch { }
        }

        private void AskTeacherPassword()
        {
            _tick.Stop();
            // 老师要走解锁流程 → 先放掉输入硬锁，否则密码框根本打不进字
            // （学生拿不到这条路径：它要靠 5 次 Esc，而 Esc 会被遮罩吞掉/计数）
            if (_inputEngaged) { InputLock.Disengage(); _inputEngaged = false; }
            CursorLock.Show();
            bool passed;
            try
            {
                // 三个措施缺一不可，共同保证"老师真的能把密码打出来"：
                //   1) _teacherDialogOpen → 让全局键盘钩子在这一刻放行（否则字母数字被自己吞掉）；
                //   2) InputLock.Disengage → 放掉系统层的 BlockInput（否则连钩子都到不了）；
                //   3) TopMost + owner    → 不会被 TopMost 的全屏遮罩压住。
                _teacherDialogOpen = true;
                Log.Info("[遮罩] 已弹出老师密码框：键盘钩子临时放行，输入硬锁 holds=" + InputLock.HoldsCount);
                using (var dlg = new PasswordDialog("LabGuard · 解除断网遮罩",
                           "输入小助手密码（解除后监控会暂停，避免马上又弹出）：", _passwordHash))
                {
                    // 两个都要：owner 让对话框永远压在遮罩（它是 TopMost）之上；TopMost 再兜一层。
                    // 只写 ShowDialog() 会出现"输密码的窗口被全屏遮罩盖住、老师找不到"。
                    dlg.TopMost = true;
                    passed = dlg.ShowDialog(this) == DialogResult.OK;
                }
            }
            finally { _teacherDialogOpen = false; }   // 无论成败都必须复位，否则遮罩会永久失去吞键能力
            if (passed)
            {
                Log.Warn("老师用密码解除了断网遮罩");
                AutoClose("老师已用密码解除");
                try { _teacherUnlock?.Invoke("密码"); } catch { }
                return;
            }
            // 关键：对话框开着这段时间，遮罩可能已经自己解除了（学生把网线插回来了）。
            // 此时若还按"取消 → 恢复上锁"的老逻辑走，就会变成
            // **遮罩没了、键鼠却锁死**（而且 _tick 一直续心跳，看门狗永远不触发）。
            if (_closed || !Visible)
            {
                Log.Info("[遮罩] 密码框取消时遮罩已自行解除，不再重新上锁");
                return;
            }
            if (!_inputEngaged) { InputLock.Engage(InputLockMode); _inputEngaged = true; }
            CursorLock.Hide();
            _tick.Start();
        }

        public void AutoClose(string reason)
        {
            // 自动解除可能由后台线程发起（网络检测线程），必须回 UI 线程动窗口
            if (Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                try { BeginInvoke(new Action(() => AutoClose(reason))); }
                catch (Exception ex) { Log.Warn("[遮罩] 自动解除跨线程投递失败：" + ex.Message); }
                return;
            }
            if (!_closed)
            {
                _closed = true;
                Log.Info("断网遮罩解除：" + reason);
            }
            // 收尾一律执行（全部幂等），不能在 _closed 时提前 return：
            // 历史坑 —— 若某条路径（如"密码框开着时网络自行恢复"）已经把 _closed 置位，
            // 而之后 AskTeacherPassword 又重启了 _tick，那些"提前 return"就会让定时器一直
            // 续心跳，输入硬锁永远解不开。这里保证无论谁来、来几次，都收干净。
            StopNetWatch();
            _tick.Stop();
            if (_inputEngaged) { InputLock.Disengage(); _inputEngaged = false; }
            CursorLock.Show();
            UninstallHook();
            Hide();
            Log.Info("[遮罩] 收尾完成（输入硬锁 holds=" + InputLock.HoldsCount + "，钩子=" +
                     (_hook == IntPtr.Zero ? "已卸" : "仍装") + "）");
        }

        private void UninstallHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _proc = null;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closed) { e.Cancel = true; return; }   // 只能用"网络恢复/老师密码"关闭
            UninstallHook();
            base.OnFormClosing(e);
        }
    }
}
