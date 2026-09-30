using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 全屏锁定屏（的"全屏锁定 / 蓝屏锁定"）：
    /// 置顶无边框、吞掉 Win/Alt+Tab/Ctrl+Esc 等键，只能输入密码或等违规条件自行解除。
    /// </summary>
    public class LockScreenForm : Form, ILockScreen
    {
        private readonly Label _titleLabel;
        private readonly Label _messageLabel;
        private readonly TextBox _password;
        private readonly Timer _watch;
        private readonly string _passwordHash;
        private readonly Panel _stage;      // 覆盖"所有显示器"的底色层
        private readonly Panel _content;    // 贴在主显示器上居中的内容层
        private Func<bool> _resolved;
        private IntPtr _hook = IntPtr.Zero;
        private bool _engaged;              // 是否已 Engage 输入硬锁（防重复上锁/重复解锁）
        private NativeMethods.LowLevelKeyboardProc _proc;

        /// <summary>
        /// 构造此窗体的线程（= UI 线程）的托管线程 ID。理由同 DisconnectMaskForm：
        /// <c>Control.InvokeRequired</c> 在句柄未创建时返回 false，Guard 的线程池线程首次
        /// 上锁就会把窗口建到没有消息循环的线程上（于是 <c>_watch</c> 永不走），
        /// 必须用线程 ID 判断。
        /// </summary>
        private readonly int _uiThreadId;

        private const int WhKeyboardLl = 13;

        /// <summary>锁屏期间是否硬锁鼠标键盘（InputLock.ModeOff / ModeOn），由 Agent 从配置注入。</summary>
        public string InputLockMode { get; set; } = InputLock.ModeOn;

        /// <summary>诊断/自检用：全局键盘钩子当前是否已安装（解锁后应为 false）。</summary>
        public bool IsHookInstalled { get { return _hook != IntPtr.Zero; } }

        public LockScreenForm(string passwordHash)
        {
            _passwordHash = passwordHash;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;   // 多屏：连非主显示器一起盖住
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(10, 20, 60);
            KeyPreview = true;
            Font = new Font("微软雅黑", 12F);

            _titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 80,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("微软雅黑", 22F, FontStyle.Bold)
            };
            _messageLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.WhiteSmoke,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("微软雅黑", 14F)
            };
            _password = new TextBox
            {
                Width = 260,
                UseSystemPasswordChar = true,
                TextAlign = HorizontalAlignment.Center
            };
            var panel = new Panel { Dock = DockStyle.Bottom, Height = 90 };
            _password.Top = 20;
            _password.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                if (PasswordHasher.Verify(_password.Text, _passwordHash))
                {
                    Unlock_AndClose();
                }
                else
                {
                    _titleLabel.Text = "密码不正确，请重新输入";
                    _password.Clear();
                }
            };
            panel.Controls.Add(_password);

            // 内容放在"主显示器大小"的容器里居中：文字不会卡在两屏接缝上
            _stage = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
            _content = new Panel { BackColor = BackColor };
            _content.Controls.Add(_messageLabel);
            _content.Controls.Add(panel);
            _content.Controls.Add(_titleLabel);
            _stage.Controls.Add(_content);
            Controls.Add(_stage);
            CenterContent();

            _watch = new Timer { Interval = 3000 };
            _watch.Tick += (s, e) =>
            {
                // 兜底：窗体已隐藏（已解锁）就停表 —— 只要不再续心跳，
                // InputLock 的 10 秒看门狗一定会把输入放开，不会把老师锁在机器外。
                if (!Visible) { _watch.Stop(); return; }
                InputLock.KeepAlive();
                if (_resolved != null)
                {
                    try { if (_resolved()) { Unlock_AndClose(); return; } }
                    catch { }
                }
            };

            // 在 UI 线程上先把句柄建好，避免 Guard 线程池线程首次上锁时把窗口建错线程
            _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            try { _ = Handle; }
            catch (Exception ex) { Log.Warn("锁屏窗体预先创建句柄失败（可能无桌面会话）：" + ex.Message); }
        }

        private static Rectangle PrimaryScreen() { return Screen.PrimaryScreen.Bounds; }
        private static Rectangle WholeDesktop() { return SystemInformation.VirtualScreen; }

        private void CenterContent()
        {
            Rectangle pa = PrimaryScreen(), va = WholeDesktop();
            _content.Bounds = new Rectangle(pa.Left - va.Left, pa.Top - va.Top, pa.Width, pa.Height);
            _password.Left = (pa.Width - _password.Width) / 2;
        }

        public LockScreenForm() : this("") { }

        /// <summary>被 Guard 调用的锁定入口（ILockScreen）。</summary>
        void ILockScreen.Show(string title, string message, bool fakeBlueScreen)
        {
            ShowLock(title, message, fakeBlueScreen, null);
        }

        void ILockScreen.Notify(string title, string message)
        {
            // Agent 里通过托盘气泡实现，这里只是兜底
        }

        public void ShowLock(string title, string message, bool fakeBlueScreen, Func<bool> conditionResolved)
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                try { BeginInvoke(new Action(() => ShowLock(title, message, fakeBlueScreen, conditionResolved))); }
                catch { }
                return;
            }
            _titleLabel.Text = title;
            _messageLabel.Text = message + "\r\n\r\n（输入老师密码可解除锁定；恢复被破坏的设置后也会自动解除）";
            Color bg = fakeBlueScreen ? Color.FromArgb(0, 40, 120) : Color.FromArgb(10, 20, 60);
            BackColor = bg;
            _stage.BackColor = bg;
            _content.BackColor = bg;
            _resolved = conditionResolved;
            _password.Clear();
            Bounds = SystemInformation.VirtualScreen;
            CenterContent();
            if (!Visible) Show();
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            CursorLock.Hide();
            if (!_engaged) { InputLock.Engage(InputLockMode); _engaged = true; }
            InstallHook();                 // 显式安装：解锁后会卸载，再次上锁必须能重装
            _watch.Start();
            _password.Focus();
        }

        public void Unlock_AndClose()
        {
            _watch.Stop();
            _resolved = null;
            if (_engaged) { InputLock.Disengage(); _engaged = false; }
            UninstallHook();               // 必须卸掉全局键盘钩子（否则解锁后 Win/Alt+Tab 仍被吞）
            CursorLock.Show();
            Hide();
        }

        /// <summary>
        /// 卸载全局键盘钩子。
        /// <para>
        /// 为什么必须在解锁时卸：<see cref="InstallHook"/> 装的是**全局** WH_KEYBOARD_LL
        /// （dwThreadId = 0），它会吞掉 Win、Alt+Tab、Alt+Esc、Alt+F4、Ctrl+Esc；
        /// 若只在进程退出时才消失，**老师输密码解锁后这些键仍会被吞掉**（只要小助手还在跑），
        /// 变成"解锁了却没法切窗口"的新麻烦。
        /// </para>
        /// </summary>
        private void UninstallHook()
        {
            if (_hook == IntPtr.Zero) return;
            try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _proc = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_engaged) { InputLock.Disengage(); _engaged = false; }
                UninstallHook();
            }
            base.Dispose(disposing);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InstallHook();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 锁定期间不允许关闭
            if (Visible) { e.Cancel = true; return; }
            base.OnFormClosing(e);
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
                        int vk = Marshal.ReadInt32(lParam);
                        // 吞掉 Win 键、Alt+Tab、Alt+Esc、Ctrl+Esc
                        bool alt = (NativeMethods_GetAsyncKeyState(0x12) & 0x8000) != 0;
                        bool ctrl = (NativeMethods_GetAsyncKeyState(0x11) & 0x8000) != 0;
                        if (vk == 0x5B || vk == 0x5C) return (IntPtr)1;
                        if (alt && (vk == 0x09 || vk == 0x1B || vk == 0x73)) return (IntPtr)1;
                        if (ctrl && vk == 0x1B) return (IntPtr)1;
                    }
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                };
                _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc, NativeMethods.GetModuleHandle(null), 0);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private static short NativeMethods_GetAsyncKeyState(int vKey) => GetAsyncKeyState(vKey);
    }
}
