using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 全屏锁定屏（对应原版的"全屏锁定 / 蓝屏锁定"）：
    /// 置顶无边框、吞掉 Win/Alt+Tab/Ctrl+Esc 等键，只能输入密码或等违规条件自行解除。
    /// </summary>
    public class LockScreenForm : Form, ILockScreen
    {
        private readonly Label _titleLabel;
        private readonly Label _messageLabel;
        private readonly TextBox _password;
        private readonly Timer _watch;
        private readonly string _passwordHash;
        private Func<bool> _resolved;
        private IntPtr _hook = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;

        private const int WhKeyboardLl = 13;

        public LockScreenForm(string passwordHash)
        {
            _passwordHash = passwordHash;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
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
            _password.Left = (Screen.PrimaryScreen.Bounds.Width - _password.Width) / 2;
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

            Controls.Add(_messageLabel);
            Controls.Add(panel);
            Controls.Add(_titleLabel);

            _watch = new Timer { Interval = 3000 };
            _watch.Tick += (s, e) =>
            {
                if (_resolved != null)
                {
                    try { if (_resolved()) { Unlock_AndClose(); return; } }
                    catch { }
                }
            };
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
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowLock(title, message, fakeBlueScreen, conditionResolved))); return; }
            _titleLabel.Text = title;
            _messageLabel.Text = message + "\r\n\r\n（输入老师密码可解除锁定；恢复被破坏的设置后也会自动解除）";
            BackColor = fakeBlueScreen ? Color.FromArgb(0, 40, 120) : Color.FromArgb(10, 20, 60);
            _resolved = conditionResolved;
            _password.Clear();
            if (!Visible) Show();
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            Cursor.Hide();
            _watch.Start();
            _password.Focus();
        }

        public void Unlock_AndClose()
        {
            _watch.Stop();
            Cursor.Show();
            Hide();
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
