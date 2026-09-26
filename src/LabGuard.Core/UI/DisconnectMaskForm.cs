using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
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
    /// · 背景可选：安装目录 wallpaper 里随机一张，或纯色提示页
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

        private const int WhKeyboardLl = 13;

        public DisconnectMaskForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
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
                    if (_networkRestored != null && _networkRestored()) { AutoClose("网络已恢复"); return; }
                    UpdateBottomLine();
                }
                catch { }
            };
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
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowMask(machineNumber, headLine, advice, passwordHash,
                    randomWallpaper, showElapsed, elapsedText, networkRestored, teacherUnlock)));
                return;
            }
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

            if (!Visible) Show();
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            Cursor.Hide();
            _tick.Start();
            InstallHook();
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
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            _card.Left = (screen.Width - _card.Width) / 2;
            _card.Top = (screen.Height - _card.Height) / 2;
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
            Cursor.Hide();
            _tick.Start();
        }

        public void AutoClose(string reason)
        {
            if (_closed) return;
            _closed = true;
            _tick.Stop();
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
