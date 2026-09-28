using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 危险操作（卸载 / 退出监控）前的"延时确认"框：
    /// 「确定」在 N 秒内不可点，按钮上显示倒计时；「取消」随时可用。
    /// 目的：防手滑误点；也让偶尔猜到密码的学生没法一秒钟把管控清空。
    /// <see cref="Confirm"/> 传入的秒数 ≤ 0 时退化成普通确认框。
    /// </summary>
    public sealed class CountdownDialog : Form
    {
        private readonly Timer _timer;
        private readonly Button _ok;
        private int _left;

        private CountdownDialog(string title, string message, int seconds)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 264);
            Font = new Font("微软雅黑", 9F);

            var label = new Label
            {
                Text = message,
                Left = 18,
                Top = 16,
                Width = 524,
                Height = 168,
                AutoSize = false
            };
            _ok = new Button { Left = 296, Top = 200, Width = 246, Height = 34 };
            var cancel = new Button
            {
                Text = "取消",
                Left = 186,
                Top = 200,
                Width = 100,
                Height = 34,
                DialogResult = DialogResult.Cancel
            };

            _left = Math.Max(0, seconds);
            _ok.Enabled = _left == 0;
            _ok.DialogResult = DialogResult.OK;
            UpdateOkText();

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                if (_left > 0) _left--;
                UpdateOkText();
                if (_left == 0) { _timer.Stop(); _ok.Enabled = true; }
            };
            if (_left > 0) _timer.Start();

            AcceptButton = _ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { label, _ok, cancel });
        }

        private void UpdateOkText()
        {
            _ok.Text = _left > 0 ? ("确定（还需 " + _left + " 秒）") : "确定";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>弹出延时确认；返回 true = 用户已确认。seconds ≤ 0 时用普通确认框。</summary>
        public static bool Confirm(string title, string message, int seconds)
        {
            if (seconds <= 0)
            {
                return MessageBox.Show(message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
            }
            using (var dlg = new CountdownDialog(title, message, seconds))
            {
                return dlg.ShowDialog() == DialogResult.OK;
            }
        }
    }
}
