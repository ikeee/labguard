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
using LabGuard.Core.UI;

namespace LabGuard.Settings
{
    /// <summary>
    /// 设置程序：主体是 <see cref="SettingsPanel"/>（左侧分组导航 + 搜索 + 只看已改动 + 改动计数，
    /// 内容由 <see cref="LabGuard.Core.Settings.SettingsCatalog"/> 驱动，共 14 组 / 92 项）。
    /// 底部另有口令、导出/导入/恢复默认/保存。
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly GuardConfig _config;
        private readonly SettingsPanel _panel;
        private readonly TextBox _pwd = new TextBox { Width = 180, UseSystemPasswordChar = true };
        private readonly TextBox _pwd2 = new TextBox { Width = 180, UseSystemPasswordChar = true };

        public SettingsForm(GuardConfig config)
        {
            _config = config;
            Text = LabGuard.Core.AppInfo.ProductName + " v" + LabGuard.Core.AppInfo.Version +
                   " · 设置（所有功能均可自行开关）";
            Font = new Font("微软雅黑", 9F);
            ClientSize = new Size(1000, 760);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 560);

            _panel = new SettingsPanel { Dock = DockStyle.Fill };
            _panel.Refresh(_config);

            // 口令（单独一行，放在开关面板下面）
            var pwdBox = new GroupBox { Dock = DockStyle.Bottom, Height = 74, Text = "口令（6 位及以上字母或数字；留空 = 不修改）" };
            var l1 = new Label { Text = "新密码：", Left = 12, Top = 26, Width = 60 };
            _pwd.Left = 74; _pwd.Top = 22;
            var l2 = new Label { Text = "再次输入：", Left = 268, Top = 26, Width = 70 };
            _pwd2.Left = 340; _pwd2.Top = 22;
            var hint = new Label
            {
                Left = 540, Top = 26, Width = 440, ForeColor = Color.DimGray,
                Text = "（口令用 PBKDF2-SHA256 保存；也是「暂停/退出/卸载」的密码）"
            };
            pwdBox.Controls.AddRange(new Control[] { l1, _pwd, l2, _pwd2, hint });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            var btnSave = new Button { Text = "保存设置(需10秒)", Left = 530, Top = 18, Width = 150, Height = 34 };
            btnSave.Click += (s, e) => Save();
            var btnExport = new Button { Text = "导出配置", Left = 690, Top = 18, Width = 90, Height = 34 };
            btnExport.Click += (s, e) => Export();
            var btnImport = new Button { Text = "导入配置", Left = 788, Top = 18, Width = 90, Height = 34 };
            btnImport.Click += (s, e) => Import();
            var btnReset = new Button { Text = "恢复默认", Left = 886, Top = 18, Width = 90, Height = 34 };
            btnReset.Click += (s, e) => ResetDefaults();
            var btnCancel = new Button { Text = "取消", Left = 440, Top = 18, Width = 80, Height = 34, DialogResult = DialogResult.Cancel };
            var lblFooter = new Label
            {
                Left = 14, Top = 14, Width = 420, Height = 50, ForeColor = Color.DimGray,
                Text = "左侧选分组；顶部可搜索、可只看已改动（与出厂默认值比较）。\r\n标红的是【危险】项，改完保存后 10 秒内生效。"
            };
            bottom.Controls.AddRange(new Control[] { btnSave, btnExport, btnImport, btnReset, btnCancel, lblFooter });

            Controls.Add(_panel);
            Controls.Add(bottom);
            Controls.Add(pwdBox);
            CancelButton = btnCancel;
        }

        // ------------------------------------------------------------------ 截图（文档/人工核对用）
        /// <summary>把 14 个分组面板逐个渲染成 PNG（不修改配置）。</summary>
        public List<string> RenderScreenshots(string outDir)
        {
            var saved = new List<string>();
            Directory.CreateDirectory(outDir);
            // 截图时临时取消 MinimumSize：否则矮的内容会被夹到 560 高、窄的会被夹到 900 宽，
            // 结果每张图都一样大、右边内容被裁掉。
            Size savedMin = MinimumSize;
            MinimumSize = Size.Empty;
            try
            {
                for (int i = 0; i < _panel.Sections.Count; i++)
                {
                    _panel.SelectSection(i);
                    Application.DoEvents();
                    Size need = _panel.PrepareForCapture();
                    ClientSize = new Size(need.Width + 6, need.Height + 74 + 76 + 6);
                    PerformLayout();
                    Application.DoEvents();
                    string path = Path.Combine(outDir,
                        string.Format("settings-{0:00}-{1}.png", i + 1, SafeName(_panel.Sections[i])));
                    if (UiCapture.Save(this, path) != null) saved.Add(path);
                    _panel.RestoreAfterCapture();
                }
            }
            finally
            {
                MinimumSize = savedMin;
            }
            return saved;
        }

        private static string SafeName(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '、' || c == ' ') sb.Append('-');
            }
            return sb.Length > 20 ? sb.ToString(0, 20) : sb.ToString();
        }

        // ------------------------------------------------------------------ 保存 / 导入导出
        private void Save()
        {
            if (!string.IsNullOrEmpty(_pwd.Text) || !string.IsNullOrEmpty(_pwd2.Text))
            {
                if (_pwd.Text != _pwd2.Text)
                {
                    MessageBox.Show("两次输入的密码不一致，请重新输入！", LabGuard.Core.AppInfo.ProductName);
                    return;
                }
                string error = PasswordHasher.Validate(_pwd.Text);
                if (error != null)
                {
                    MessageBox.Show(error, LabGuard.Core.AppInfo.ProductName);
                    return;
                }
                _config.PasswordHash = PasswordHasher.Create(_pwd.Text);
            }
            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                MessageBox.Show("请设置 6 位及以上的字母或数字作为口令！", LabGuard.Core.AppInfo.ProductName);
                return;
            }

            _panel.Collect(_config);
            if (_config.Network.EnforceDns && _config.Network.DnsServers.Count == 0)
            {
                MessageBox.Show("勾选了「锁定学生机 DNS」，但没有填 DNS 服务器地址。\r\n请填服务器 IP 或取消勾选。",
                    LabGuard.Core.AppInfo.ProductName);
                return;
            }

            try
            {
                ConfigStore.Save(_config);
                WatchdogGuard.MarkPaused(false);
                _pwd.Clear();
                _pwd2.Clear();
                _panel.Refresh(_config);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message + "\r\n请右键设置程序，选择【以管理员身份运行】后重试。",
                    LabGuard.Core.AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RestartEngineSide();
            MessageBox.Show("设置已保存，监控将在 10 秒内重新生效。\r\n（记得在硬盘保护系统中创建进度 / 保存系统）",
                LabGuard.Core.AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                _panel.Collect(_config);
                using (var dlg = new SaveFileDialog
                {
                    Filter = "配置文件|*.json",
                    FileName = "labguard-config-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json"
                })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, ConfigStore.Serialize(_config), new System.Text.UTF8Encoding(false));
                    MessageBox.Show("已导出（含口令散列，可直接拷到其它学生机导入）：\r\n" + dlg.FileName,
                        LabGuard.Core.AppInfo.ProductName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, LabGuard.Core.AppInfo.ProductName);
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
                    string keepPassword = _config.PasswordHash ?? "";
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
                    _config.Site = imported.Site;
                    _config.RegistryAcl = imported.RegistryAcl;
                    _config.Watchdog = imported.Watchdog;
                    _panel.Refresh(_config);
                    MessageBox.Show("已导入，请检查各分组后点【保存设置】。", LabGuard.Core.AppInfo.ProductName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导入失败：" + ex.Message, LabGuard.Core.AppInfo.ProductName);
            }
        }

        private void ResetDefaults()
        {
            if (MessageBox.Show("把所有开关恢复为程序默认值？\r\n（口令不会被清除）", LabGuard.Core.AppInfo.ProductName,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
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
            _config.Site = fresh.Site;
            _config.RegistryAcl = fresh.RegistryAcl;
            _config.Watchdog = fresh.Watchdog;
            _panel.Refresh(_config);
        }
    }
}
