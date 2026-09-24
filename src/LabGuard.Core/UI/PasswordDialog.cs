using System;
using System.Drawing;
using System.Windows.Forms;
using LabGuard.Core.Config;

namespace LabGuard.Core.UI
{
    /// <summary>统一的密码输入框（退出/暂停/设置/卸载都用它）。</summary>
    public class PasswordDialog : Form
    {
        private readonly TextBox _box;
        private readonly Label _hint;

        public string Password { get; private set; }

        public PasswordDialog(string title, string prompt, string passwordHash, bool confirmNew = false)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(380, 150);
            Font = new Font("微软雅黑", 9F);

            var label = new Label { Text = prompt, Left = 16, Top = 18, Width = 340, Height = 22 };
            _box = new TextBox { Left = 16, Top = 46, Width = 340, UseSystemPasswordChar = true };
            _hint = new Label { Left = 16, Top = 74, Width = 340, Height = 20, ForeColor = Color.Firebrick };
            var ok = new Button { Text = "确 定", Left = 194, Top = 104, Width = 78, Height = 28, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "取 消", Left = 278, Top = 104, Width = 78, Height = 28, DialogResult = DialogResult.Cancel };

            ok.Click += (s, e) =>
            {
                string pwd = _box.Text;
                if (confirmNew)
                {
                    string error = PasswordHasher.Validate(pwd);
                    if (error != null) { _hint.Text = error; return; }
                }
                else
                {
                    if (string.IsNullOrEmpty(pwd)) { _hint.Text = "请输入密码"; return; }
                    if (!string.IsNullOrEmpty(passwordHash) && !PasswordHasher.Verify(pwd, passwordHash))
                    {
                        _hint.Text = "密码不正确，请重新输入！";
                        _box.Clear();
                        return;
                    }
                }
                Password = pwd;
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[] { label, _box, _hint, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
