using System;
using System.Drawing;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.UI
{
    /// <summary>统一的密码输入框（退出/暂停/设置/卸载都用它）。</summary>
    public class PasswordDialog : Form
    {
        private readonly TextBox _box;
        private readonly Label _hint;

        public string Password { get; private set; }

        /// <summary>本次框内「提交了但校验失败」的次数。0 = 老师压根没试、只是关掉了窗口。</summary>
        public int FailedAttempts { get; private set; }

        public PasswordDialog(string title, string prompt, string passwordHash, bool confirmNew = false)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            // 置顶：本框常被用来盖在 TopMost 的全屏窗口（断网遮罩/锁定屏）之上，
            // 不置顶就会被它们压住，出现"老师看不到输密码的窗口"。
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(380, 172);
            Font = new Font("微软雅黑", 9F);

            var label = new Label { Text = prompt, Left = 16, Top = 18, Width = 340, Height = 22 };
            _box = new TextBox { Left = 16, Top = 46, Width = 340, UseSystemPasswordChar = true };
            _hint = new Label { Left = 16, Top = 74, Width = 340, Height = 20, ForeColor = Color.Firebrick };
            // 忘记密码只指向"找管理员"，绝不在这里写救援命令/路径：
            // 这个框学生也看得到，写命令=把后门贴给学生（救援办法只印在老师自己的介质上）。
            var tip = new Label
            {
                Text = PasswordGate.ForgotPasswordTip,
                Left = 16, Top = 100, Width = 340, Height = 18,
                ForeColor = Color.DimGray,
                Font = new Font("微软雅黑", 8.25F)
            };
            var ok = new Button { Text = "确 定", Left = 194, Top = 128, Width = 78, Height = 28, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "取 消", Left = 278, Top = 128, Width = 78, Height = 28, DialogResult = DialogResult.Cancel };

            ok.Click += (s, e) =>
            {
                string pwd = _box.Text;
                // 记录"提交内容长度"是为了能判断**按键到底有没有真正进到输入框**：
                // 这类框常被盖在 TopMost 的全屏窗口（断网遮罩 / 锁定屏）之上，
                // 一旦某个全局键盘钩子没放行，老师的按键会被吞掉 —— 那时长度就是 0，
                // 日志里一眼可见（否则现场只表现为"密码打不进去"，根本没法查）。
                if (string.IsNullOrEmpty(pwd))
                {
                    _hint.Text = "请输入密码";
                    Log.Warn("口令为空就点了确定（" + Text + "）");
                    return;
                }
                if (confirmNew)
                {
                    string error = PasswordHasher.Validate(pwd);
                    if (error != null) { _hint.Text = error; return; }
                }
                else if (!string.IsNullOrEmpty(passwordHash) && !PasswordHasher.Verify(pwd, passwordHash))
                {
                    FailedAttempts++;                    // 让调用方能区分「输错」和「直接取消」
                    _hint.Text = "密码不正确（已试 " + FailedAttempts + " 次）";
                    Log.Warn("口令校验失败（" + Text + "）：提交内容长度 " + pwd.Length);
                    _box.Clear();
                    return;
                }
                Password = pwd;
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.AddRange(new Control[] { label, _box, _hint, tip, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;

            // 必须显式把焦点给到输入框：这类框是从全屏窗口（TopMost）之上弹出的，
            // 默认焦点可能落在按钮上，老师就得先点一下输入框才能打字 —— 很容易被当成"打不出字"。
            Shown += (s, e) => { Activate(); _box.Focus(); _box.Select(0, 0); };
        }
    }
}
