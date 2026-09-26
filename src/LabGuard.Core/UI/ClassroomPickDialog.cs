using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Interop;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 「课堂软件学生端」选择框：自动识别极域 / 红蜘蛛 / 锐捷云课堂等客户端，
    /// 识别到就列表选择；**识别不到就让老师手动填写或浏览选择**。
    /// </summary>
    public class ClassroomPickDialog : Form
    {
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _manual = new TextBox();
        private readonly Label _hint = new Label();
        private List<ClassroomCandidate> _candidates = new List<ClassroomCandidate>();

        public string SelectedPath { get; private set; }

        public ClassroomPickDialog(string current)
        {
            Text = "识别课堂软件学生端";
            Font = new Font("微软雅黑", 9F);
            ClientSize = new Size(720, 380);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var title = new Label
            {
                Text = "正在识别的软件：极域电子教室 / 红蜘蛛 / 锐捷云课堂 / 联想云课堂 等",
                Left = 14, Top = 12, Width = 690
            };
            _list.Left = 14; _list.Top = 40; _list.Width = 690; _list.Height = 180;
            _hint.Left = 14; _hint.Top = 226; _hint.Width = 690; _hint.Height = 34;
            _hint.ForeColor = Color.Firebrick;

            var lblManual = new Label { Text = "手动填写路径：", Left = 14, Top = 268, Width = 100 };
            _manual.Left = 112; _manual.Top = 264; _manual.Width = 470; _manual.Text = current ?? "";
            var browse = new Button { Text = "浏览…", Left = 590, Top = 262, Width = 114, Height = 26 };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "学生端程序|*.exe", Title = "选择课堂软件学生端程序" })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        _manual.Text = dlg.FileName;
                        _list.ClearSelected();
                    }
                }
            };

            var ok = new Button { Text = "确定", Left = 520, Top = 320, Width = 88, Height = 30, DialogResult = DialogResult.None };
            var cancel = new Button { Text = "取消", Left = 616, Top = 320, Width = 88, Height = 30, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                string path = _list.SelectedIndex >= 0 && _list.SelectedIndex < _candidates.Count
                    ? _candidates[_list.SelectedIndex].Path
                    : _manual.Text.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show("请选择列表中的一项，或手动填写学生端程序路径。", "识别课堂软件");
                    return;
                }
                if (!File.Exists(path))
                {
                    if (MessageBox.Show("该路径当前不存在：\r\n" + path + "\r\n\r\n仍要保存吗？", "识别课堂软件",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
                SelectedPath = path;
                DialogResult = DialogResult.OK;
                Close();
            };

            _list.SelectedIndexChanged += (s, e) =>
            {
                if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _candidates.Count)
                    _manual.Text = _candidates[_list.SelectedIndex].Path;
            };

            Controls.AddRange(new Control[] { title, _list, _hint, lblManual, _manual, browse, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => Detect();
        }

        private void Detect()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                _candidates = ClassroomDetector.DetectAll();
            }
            catch { }
            finally { Cursor = Cursors.Default; }

            _list.Items.Clear();
            if (_candidates != null && _candidates.Count > 0)
            {
                foreach (ClassroomCandidate c in _candidates) _list.Items.Add(c.ToString());
                _list.SelectedIndex = 0;
                _hint.ForeColor = Color.SeaGreen;
                _hint.Text = "已识别到 " + _candidates.Count + " 个候选，直接选一个即可；也可以手动填写别的路径。";
            }
            else
            {
                _hint.ForeColor = Color.Firebrick;
                _hint.Text = "未识别到课堂软件（可能还没安装、或没在运行）。请手动填写学生端程序路径，或点“浏览…”选择。";
                _manual.Focus();
            }
        }

        /// <summary>便捷入口：返回选中的路径（取消返回 null）。</summary>
        public static string Pick(IWin32Window owner, string current)
        {
            using (var dlg = new ClassroomPickDialog(current))
            {
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }
    }
}
