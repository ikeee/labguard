using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;
using LabGuard.Core.Settings;

namespace LabGuard.Settings
{
    /// <summary>
    /// 设置程序：界面完全由 <see cref="SettingsCatalog"/> 渲染，
    /// 因此 GuardConfig 里**每一个**可配置项都有对应的开关（自检强制保证，不会漏项）。
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly GuardConfig _config;
        private readonly FlowLayoutPanel _panel;
        private readonly List<KeyValuePair<FieldSpec, Control>> _bindings = new List<KeyValuePair<FieldSpec, Control>>();

        public SettingsForm(GuardConfig config)
        {
            _config = config;
            Text = LabGuard.Core.AppInfo.ProductName + " v" + LabGuard.Core.AppInfo.Version +
                   " · 设置（所有功能均可自行开关）";
            Font = new Font("微软雅黑", 9F);
            ClientSize = new Size(900, 760);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 520);

            _panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };

            foreach (string section in SettingsCatalog.All().Select(f => f.Section).Distinct())
            {
                var fields = SettingsCatalog.All().Where(f => f.Section == section).ToList();
                _panel.Controls.Add(BuildSection(section, fields));
            }
            _panel.Controls.Add(BuildPasswordSection());

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            var btnSave = new Button { Text = "保存设置(需10秒)", Left = 430, Top = 18, Width = 150, Height = 34 };
            btnSave.Click += (s, e) => Save();
            var btnExport = new Button { Text = "导出配置", Left = 590, Top = 18, Width = 90, Height = 34 };
            btnExport.Click += (s, e) => Export();
            var btnImport = new Button { Text = "导入配置", Left = 688, Top = 18, Width = 90, Height = 34 };
            btnImport.Click += (s, e) => Import();
            var btnReset = new Button { Text = "恢复默认", Left = 786, Top = 18, Width = 90, Height = 34 };
            btnReset.Click += (s, e) => ResetDefaults();
            var btnCancel = new Button { Text = "取消", Left = 340, Top = 18, Width = 80, Height = 34, DialogResult = DialogResult.Cancel };
            var lblFooter = new Label
            {
                Text = "注：所有开关都由你决定；标【危险】的项会让一节普通的网线松动变成关机/重启，默认关闭。\r\n" +
                       "改完保存后 10 秒内生效；设置前建议先在硬盘保护系统中保存进度。",
                Left = 14,
                Top = 14,
                Width = 320,
                Height = 50,
                ForeColor = Color.DimGray
            };
            bottom.Controls.AddRange(new Control[] { btnSave, btnExport, btnImport, btnReset, btnCancel, lblFooter });

            Controls.Add(_panel);
            Controls.Add(bottom);
            CancelButton = btnCancel;
        }

        // ------------------------------------------------------------------ 渲染
        private GroupBox BuildSection(string title, List<FieldSpec> fields)
        {
            var box = new GroupBox
            {
                Text = title,
                Width = 855,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(3, 3, 3, 10),
                Padding = new Padding(10)
            };
            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));

            foreach (FieldSpec f in fields)
            {
                Control control = BuildControl(f);
                var label = new Label
                {
                    Text = f.Label + (string.IsNullOrEmpty(f.Hint) ? "" : "\r\n" + f.Hint),
                    AutoSize = true,
                    MaximumSize = new Size(465, 0),
                    ForeColor = f.Dangerous ? Color.Firebrick : Color.Black
                };
                table.Controls.Add(label, 0, table.RowCount);
                table.Controls.Add(control, 1, table.RowCount);
                table.RowCount++;
                _bindings.Add(new KeyValuePair<FieldSpec, Control>(f, control));
            }
            box.Controls.Add(table);
            return box;
        }

        private Control BuildControl(FieldSpec f)
        {
            object current = f.Get(_config);
            switch (f.Kind)
            {
                case FieldKind.Bool:
                    return new CheckBox { Checked = current is bool && (bool)current, Text = "开关", AutoSize = true };
                case FieldKind.Choice:
                {
                    var combo = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
                    combo.Items.AddRange(f.Choices.Cast<object>().ToArray());
                    string now = Convert.ToString(current);
                    int idx = Array.IndexOf(f.Values, now);
                    combo.SelectedIndex = idx >= 0 ? idx : 0;
                    return combo;
                }
                case FieldKind.Number:
                {
                    var num = new NumericUpDown { Width = 120, Minimum = f.Min, Maximum = f.Max };
                    decimal v;
                    num.Value = decimal.TryParse(Convert.ToString(current), out v)
                        ? Math.Min(Math.Max(v, f.Min), f.Max) : f.Min;
                    return num;
                }
                case FieldKind.List:
                {
                    var memo = new TextBox { Multiline = true, Width = 330, Height = Math.Max(60, 18 * Math.Min(6, Lines(current).Length + 1)), ScrollBars = ScrollBars.Vertical };
                    memo.Text = string.Join("\r\n", Lines(current));
                    return memo;
                }
                case FieldKind.Path:
                {
                    var wrap = new Panel { Width = 330, Height = 30 };
                    var text = new TextBox { Width = 240, Text = Convert.ToString(current) ?? "" };
                    var browse = new Button { Text = "浏览…", Left = 246, Width = 80, Height = 24 };
                    browse.Click += (s, e) =>
                    {
                        using (var dlg = new OpenFileDialog { Filter = f.Filter ?? "所有文件|*.*" })
                        {
                            if (dlg.ShowDialog() == DialogResult.OK) text.Text = dlg.FileName;
                        }
                    };
                    wrap.Controls.Add(text);
                    wrap.Controls.Add(browse);
                    return wrap;
                }
                default:
                    return new TextBox { Width = 320, Text = Convert.ToString(current) ?? "" };
            }
        }

        private static string[] Lines(object value)
        {
            var list = value as IEnumerable<string>;
            return list == null ? new string[0] : list.ToArray();
        }

        private GroupBox BuildPasswordSection()
        {
            var box = new GroupBox
            {
                Text = "14、密码（6 位及以上字母或数字；留空 = 不修改）",
                Width = 855,
                Height = 90,
                Margin = new Padding(3, 3, 3, 10)
            };
            var lbl = new Label { Text = "密码：                      再次输入：", Left = 12, Top = 12, Width = 420 };
            var pwd = new TextBox { Left = 12, Top = 34, Width = 200, UseSystemPasswordChar = true };
            var pwd2 = new TextBox { Left = 224, Top = 34, Width = 200, UseSystemPasswordChar = true };
            var hint = new Label
            {
                Text = "（原版默认口令是 111111，复刻版用 PBKDF2-SHA256 保存；这个密码也是「暂停/退出/卸载」的密码）",
                Left = 12,
                Top = 60,
                Width = 700,
                ForeColor = Color.DimGray
            };
            box.Controls.AddRange(new Control[] { lbl, pwd, pwd2, hint });
            _pwdText = pwd;
            _pwdText2 = pwd2;
            return box;
        }

        private TextBox _pwdText;
        private TextBox _pwdText2;

        // ------------------------------------------------------------------ 保存 / 导入导出
        private void Collect()
        {
            foreach (var kv in _bindings)
            {
                FieldSpec f = kv.Key;
                Control c = kv.Value;
                switch (f.Kind)
                {
                    case FieldKind.Bool:
                        f.Set(_config, ((CheckBox)c).Checked);
                        break;
                    case FieldKind.Choice:
                    {
                        int idx = ((ComboBox)c).SelectedIndex;
                        f.Set(_config, f.Values[Math.Max(0, idx)]);
                        break;
                    }
                    case FieldKind.Number:
                        f.Set(_config, (int)((NumericUpDown)c).Value);
                        break;
                    case FieldKind.List:
                        f.Set(_config, ((TextBox)c).Lines
                            .Select(x => x.Trim())
                            .Where(x => x.Length > 0)
                            .ToList());
                        break;
                    case FieldKind.Path:
                        f.Set(_config, c.Controls.OfType<TextBox>().First().Text.Trim());
                        break;
                    default:
                        f.Set(_config, ((TextBox)c).Text.Trim());
                        break;
                }
            }
        }

        private void RefreshControls()
        {
            foreach (var kv in _bindings)
            {
                FieldSpec f = kv.Key;
                Control c = kv.Value;
                object v = f.Get(_config);
                switch (f.Kind)
                {
                    case FieldKind.Bool: ((CheckBox)c).Checked = v is bool && (bool)v; break;
                    case FieldKind.Choice:
                        int idx = Array.IndexOf(f.Values, Convert.ToString(v));
                        ((ComboBox)c).SelectedIndex = idx >= 0 ? idx : 0;
                        break;
                    case FieldKind.Number:
                        decimal d;
                        ((NumericUpDown)c).Value = decimal.TryParse(Convert.ToString(v), out d)
                            ? Math.Min(Math.Max(d, f.Min), f.Max) : f.Min;
                        break;
                    case FieldKind.List: ((TextBox)c).Text = string.Join("\r\n", Lines(v)); break;
                    case FieldKind.Path: c.Controls.OfType<TextBox>().First().Text = Convert.ToString(v) ?? ""; break;
                    default: ((TextBox)c).Text = Convert.ToString(v) ?? ""; break;
                }
            }
            _pwdText.Clear();
            _pwdText2.Clear();
        }

        private void Save()
        {
            if (!string.IsNullOrEmpty(_pwdText.Text) || !string.IsNullOrEmpty(_pwdText2.Text))
            {
                if (_pwdText.Text != _pwdText2.Text)
                {
                    MessageBox.Show("两次输入的密码不一致，请重新输入！", "机房管理助手");
                    return;
                }
                string error = PasswordHasher.Validate(_pwdText.Text);
                if (error != null)
                {
                    MessageBox.Show(error, "机房管理助手");
                    return;
                }
                _config.PasswordHash = PasswordHasher.Create(_pwdText.Text);
            }
            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                MessageBox.Show("请设置 6 位及以上的字母或数字作为小助手密码！", "机房管理助手");
                return;
            }

            Collect();
            if (_config.Network.EnforceDns && _config.Network.DnsServers.Count == 0)
            {
                MessageBox.Show("勾选了「锁定学生机 DNS」，但没有填 DNS 服务器地址。\r\n请填教师机 IP 或取消勾选。",
                    "机房管理助手");
                return;
            }

            try
            {
                ConfigStore.Save(_config);
                WatchdogGuard.MarkPaused(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message + "\r\n请右击设置程序，选择【以管理员身份运行】后重试。",
                    "机房管理助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RestartEngineSide();
            MessageBox.Show("设置已保存，监控将在 10 秒内重新生效。\r\n" +
                            "（记得在硬盘保护系统中创建进度 / 保存系统）",
                "机房管理助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void RestartEngineSide()
        {
            SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName);
            SystemActions.Run("sc.exe", "start " + WatchdogGuard.ServiceName);
            try
            {
                foreach (Process p in Process.GetProcessesByName("LabGuard.Agent")) p.Kill();
            }
            catch { }
            try
            {
                string agent = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabGuard.Agent.exe");
                if (File.Exists(agent)) Process.Start(new ProcessStartInfo(agent) { UseShellExecute = true });
            }
            catch { }
        }

        private void Export()
        {
            try
            {
                Collect();
                using (var dlg = new SaveFileDialog
                {
                    Filter = "配置文件|*.json",
                    FileName = "labguard-config-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json"
                })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    string json = ConfigStore.Serialize(_config);
                    File.WriteAllText(dlg.FileName, json, new System.Text.UTF8Encoding(false));
                    MessageBox.Show("已导出（含口令散列，可直接拷到其它学生机导入）：\r\n" + dlg.FileName,
                        "机房管理助手");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "机房管理助手");
            }
        }

        private void Import()
        {
            try
            {
                using (var dlg = new OpenFileDialog { Filter = "配置文件|*.json" })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    GuardConfig imported = ConfigStore.ImportFrom(dlg.FileName);
                    // 覆盖叶子字段：用反射整体复制（保持对象引用不变，界面绑定仍然有效）
                    string keepPassword = _config.PasswordHash == null ? "" : _config.PasswordHash;
                    _config.Enabled = imported.Enabled;
                    _config.ResumeAfterMinutes = imported.ResumeAfterMinutes;
                    _config.StartDelaySeconds = imported.StartDelaySeconds;
                    _config.PasswordHash = string.IsNullOrEmpty(imported.PasswordHash) ? keepPassword : imported.PasswordHash;
                    _config.Classroom = imported.Classroom;
                    _config.Network = imported.Network;
                    _config.ProcessBlock = imported.ProcessBlock;
                    _config.FileCreation = imported.FileCreation;
                    _config.Usb = imported.Usb;
                    _config.Hosts = imported.Hosts;
                    _config.Browser = imported.Browser;
                    _config.Shell = imported.Shell;
                    _config.SafeMode = imported.SafeMode;
                    _config.Wallpaper = imported.Wallpaper;
                    _config.RegistryAcl = imported.RegistryAcl;
                    _config.Watchdog = imported.Watchdog;
                    RefreshControls();
                    MessageBox.Show("已导入，请检查各分组后点【保存设置】。", "机房管理助手");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, "机房管理助手");
            }
        }

        private void ResetDefaults()
        {
            if (MessageBox.Show("把所有开关恢复为程序默认值？\r\n（密码不会被清除）",
                    "机房管理助手", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            string pwd = _config.PasswordHash;
            var fresh = new GuardConfig { PasswordHash = pwd };
            _config.Enabled = fresh.Enabled;
            _config.ResumeAfterMinutes = fresh.ResumeAfterMinutes;
            _config.StartDelaySeconds = fresh.StartDelaySeconds;
            _config.Classroom = fresh.Classroom;
            _config.Network = fresh.Network;
            _config.ProcessBlock = fresh.ProcessBlock;
            _config.FileCreation = fresh.FileCreation;
            _config.Usb = fresh.Usb;
            _config.Hosts = fresh.Hosts;
            _config.Browser = fresh.Browser;
            _config.Shell = fresh.Shell;
            _config.SafeMode = fresh.SafeMode;
            _config.Wallpaper = fresh.Wallpaper;
            _config.RegistryAcl = fresh.RegistryAcl;
            _config.Watchdog = fresh.Watchdog;
            RefreshControls();
        }
    }
}
