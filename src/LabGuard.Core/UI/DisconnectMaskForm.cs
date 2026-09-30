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
        private Action _teacherUnlock;
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

        /// <summary>遮罩期间是否硬锁鼠标键盘（InputLock.ModeOff / ModeOn），由 Agent 从配置注入。</summary>
        public string InputLockMode { get; set; } = InputLock.ModeOn;

        private bool _inputEngaged;

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
            _number.Text = text;
        }

        public void ShowMask(string machineNumber, string headLine, string advice, string passwordHash,
            bool randomWallpaper, bool showElapsed, Func<string> elapsedText, Func<bool> networkRestored,
            Action teacherUnlock)
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

            _title.Text = headLine;
            _hint.Text = advice + Environment.NewLine + Environment.NewLine +
                         "（恢复网络后 10 秒内自动消失；老师连续按 5 次 Esc 可输入密码解除）";
            _machineNumber = machineNumber;
            UpdateBottomLine();

            LoadBackground(randomWallpaper);
            RelayoutCard();

            Bounds = SystemInformation.VirtualScreen;
            if (!_inputEngaged) { InputLock.Engage(InputLockMode); _inputEngaged = true; }
            if (!Visible) Show();
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            Cursor.Hide();
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
                        int msg = wParam.ToInt32();
                        if (msg == 0x0100 || msg == 0x0101 || msg == 0x0104 || msg == 0x0105)
                        {
                            int vk = Marshal.ReadInt32(lParam) & 0xFF;
                            if (vk == 0x1B) // Esc：作为老师解锁入口，但仍不进系统
                            {
                                TrackEsc();
                                return (IntPtr)1;
                            }
                            TrackEscReset();
                            return (IntPtr)1; // 其它键全部吞掉
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
            AskTeacherPassword();
        }

        private void TrackEscReset() { _escCount = 0; }

        private void AskTeacherPassword()
        {
            _tick.Stop();
            // 老师要走解锁流程 → 先放掉输入硬锁，否则密码框根本打不进字
            // （学生拿不到这条路径：它要靠 5 次 Esc，而 Esc 会被遮罩吞掉/计数）
            if (_inputEngaged) { InputLock.Disengage(); _inputEngaged = false; }
            Cursor.Show();
            using (var dlg = new PasswordDialog("LabGuard · 解除断网遮罩",
                       "输入小助手密码（解除后监控会暂停，避免马上又弹出）：", _passwordHash))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    Log.Warn("老师用密码解除了断网遮罩");
                    AutoClose("老师已用密码解除");
                    try { _teacherUnlock?.Invoke(); } catch { }
                    return;
                }
            }
            if (!_inputEngaged) { InputLock.Engage(InputLockMode); _inputEngaged = true; }
            Cursor.Hide();
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
            if (_closed) return;
            _closed = true;
            StopNetWatch();
            _tick.Stop();
            if (_inputEngaged) { InputLock.Disengage(); _inputEngaged = false; }
            Cursor.Show();
            if (_hook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            Log.Info("断网遮罩解除：" + reason);
            Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closed) { e.Cancel = true; return; }   // 只能用"网络恢复/老师密码"关闭
            if (_hook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
            base.OnFormClosing(e);
        }
    }
}
