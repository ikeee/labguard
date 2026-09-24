using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Settings;
using LabGuard.Core.UI;

namespace LabGuard.Installer
{
    /// <summary>向导：欢迎 → 安装位置 → 密码 → 功能开关（90+ 项）→ 安装。</summary>
    public class InstallerForm : Form
    {
        private readonly TabControl _tabs = new TabControl();
        private readonly Button _btnBack = new Button { Text = "上一步", Width = 90, Height = 32 };
        private readonly Button _btnNext = new Button { Text = "下一步", Width = 110, Height = 32 };
        private readonly Button _btnCancel = new Button { Text = "取消", Width = 80, Height = 32 };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };

        private readonly TextBox _dir = new TextBox { Width = 520 };
        private readonly RadioButton _dirDefault = new RadioButton { Text = "推荐：系统程序目录（普通用户不可写，便于统一维护）" };
        private readonly RadioButton _dirCustom = new RadioButton { Text = "自定义：" };
        private readonly TextBox _pwd = new TextBox { Width = 220, UseSystemPasswordChar = true };
        private readonly TextBox _pwd2 = new TextBox { Width = 220, UseSystemPasswordChar = true };
        private readonly SettingsPanel _options = new SettingsPanel();
        private readonly ComboBox _presetBox = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _optionCount = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly TextBox _progress = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Width = 780, Height = 260, Font = new Font("Consolas", 9F)
        };
        private readonly CheckBox _startNow = new CheckBox { Text = "安装完成后立即启用监控", Checked = true, Width = 400 };
        private readonly CheckBox _agree = new CheckBox
        {
            Text = "我已阅读并了解：本程序会限制下载/小游戏/任务管理器/注册表/U盘等，并已在机房管理规定中说明",
            Width = 760
        };
        private bool _installed;

        public InstallerForm()
        {
            Text = LabGuard.Core.AppInfo.ProductName + " v" + LabGuard.Core.AppInfo.Version + " · 一键安装向导";
            Font = new Font("微软雅黑", 9F);
            ClientSize = new Size(900, 660);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            _tabs.Dock = DockStyle.Fill;
            _tabs.Appearance = TabAppearance.Normal;
            _tabs.TabPages.Add(TabWelcome());
            _tabs.TabPages.Add(TabLocation());
            _tabs.TabPages.Add(TabPassword());
            _tabs.TabPages.Add(TabOptions());
            _tabs.TabPages.Add(TabInstall());

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 56 };
            _btnBack.Left = 540; _btnBack.Top = 12; _btnBack.Click += (s, e) => Go(-1);
            _btnNext.Left = 640; _btnNext.Top = 12; _btnNext.Click += (s, e) => Go(1);
            _btnCancel.Left = 760; _btnCancel.Top = 12; _btnCancel.Click += (s, e) => Close();
            _status.Left = 16; _status.Top = 20;
            footer.Controls.AddRange(new Control[] { _status, _btnBack, _btnNext, _btnCancel });

            Controls.Add(_tabs);
            Controls.Add(footer);
            Load += (s, e) => UpdateButtons();
        }

        // ------------------------------------------------------------------ 各页
        private TabPage TabWelcome()
        {
            var page = new TabPage("1. 欢迎") { Padding = new Padding(16) };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
                Font = new Font("微软雅黑", 10F),
                Text = string.Join(Environment.NewLine, new[]
                {
                    "感谢使用【LabGuard】。",
                    "",
                    "这是一套面向学校计算机机房的本地管控程序：保护电子教室（极域/红蜘蛛/锐捷云课堂）不被学生脱离控制，",
                    "规范上机行为（下载、小游戏、任务管理器/注册表、U 盘、浏览器下载等），并在教师机部署 AdGuard 时配合集中 DNS 过滤。",
                    "",
                    "安装程序会做这些事：",
                    "  · 释放程序文件到安装目录（默认 C:\\Program Files\\LabGuard）",
                    "  · 把该目录权限收紧为「管理员/SYSTEM 完全控制，普通用户只读」",
                    "  · 注册 Windows 服务 LabGuardSvc（守护 + 系统级策略）",
                    "  · 创建“登录时以最高权限运行小助手”的计划任务（不需要修改 UAC 设置）",
                    "  · 创建桌面/开始菜单快捷方式、登记“控制面板→卸载”",
                    "  · 写入你下一步选择的策略与密码",
                    "",
                    "与最大的不同（请放心）：",
                    "  · 不要求卸载杀毒软件、不关闭 Windows Defender（建议把安装目录加入杀软白名单即可）",
                    "  · 不劫持浏览器主页、不冒充其它软件、不隐藏改动痕迹",
                    "  · 老师随时可用密码暂停/退出，卸载程序会把所有改动逐项还原",
                    "",
                    "建议：先在 1 台机器上试运行一周（含一次真实课堂），确认与考试软件、mpython、3D One 等兼容后再整机房部署。",
                    "注意：装好后默认禁用 U 盘——所以请先把安装包拷进机器，再安装。"
                })
            };
            var agree = _agree;
            agree.Dock = DockStyle.Bottom;
            page.Controls.Add(box);
            page.Controls.Add(agree);
            return page;
        }

        private TabPage TabLocation()
        {
            var page = new TabPage("2. 安装位置") { Padding = new Padding(20) };
            string def = InstallEngine.DefaultInstallDir();

            _dirDefault.Checked = true;
            _dirDefault.Top = 20; _dirDefault.Left = 20; _dirDefault.Width = 760;
            var lblDef = new Label { Text = "    实际路径：" + def, Left = 44, Top = 48, Width = 760, ForeColor = Color.DimGray };
            _dirCustom.Top = 90; _dirCustom.Left = 20; _dirCustom.Width = 120;
            _dir.Text = def;
            _dir.Left = 140; _dir.Top = 88; _dir.Enabled = false;
            var browse = new Button { Text = "浏览…", Left = 670, Top = 86, Width = 80, Height = 26, Enabled = false };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        _dir.Text = Path.Combine(dlg.SelectedPath, "LabGuard");
                    }
                }
            };
            _dirCustom.CheckedChanged += (s, e) =>
            {
                _dir.Enabled = _dirCustom.Checked;
                browse.Enabled = _dirCustom.Checked;
                if (!_dirCustom.Checked) _dir.Text = def;
            };
            var hint = new Label
            {
                Left = 20, Top = 140, Width = 800, Height = 90, ForeColor = Color.DimGray,
                Text = "· 默认装在系统程序目录：普通用户不可写，也不能随手删除；路径固定，便于统一维护。\n" +
                       "· 若学校习惯统一目录（便于维护、便于组策略下发），请选“自定义”，例如 C:\\LabGuard。\n" +
                       "· 无论选哪个，安装目录都会自动收紧权限，普通用户只能读、不能改。"
            };
            page.Controls.AddRange(new Control[] { _dirDefault, lblDef, _dirCustom, _dir, browse, hint });
            return page;
        }

        private TabPage TabPassword()
        {
            var page = new TabPage("3. 设置密码") { Padding = new Padding(20) };
            var l1 = new Label { Text = "小助手密码：", Left = 20, Top = 30, Width = 110 };
            _pwd.Left = 140; _pwd.Top = 28;
            var l2 = new Label { Text = "再次输入：", Left = 20, Top = 70, Width = 110 };
            _pwd2.Left = 140; _pwd2.Top = 68;
            var hint = new Label
            {
                Left = 20, Top = 120, Width = 820, Height = 160, ForeColor = Color.DimGray,
                Text = "· 6 位及以上字母或数字；不要用 123456 这类弱口令。\n" +
                       "· 这个密码用于：暂停监控、退出程序、修改设置、解除断网遮罩、卸载。\n" +
                       "· 密码只保存 PBKDF2-SHA256 散列（10 万次迭代 + 随机盐），程序里看不到明文。\n" +
                       "· 建议不要与极域电子教室的密码相同（极域密码容易被学生查到）。\n" +
                       "· 忘记密码时：用管理员运行安装目录里的 LabGuard.Uninstall.exe 可强制卸载并还原。"
            };
            page.Controls.AddRange(new Control[] { l1, _pwd, l2, _pwd2, hint });
            return page;
        }

        private TabPage TabOptions()
        {
            var page = new TabPage("4. 功能开关（90+ 项）") { Padding = new Padding(10) };
            var top = new Panel { Dock = DockStyle.Top, Height = 76 };
            var lbl = new Label { Text = "先套用一个预设，再按需逐项调整：", Left = 6, Top = 8, Width = 330 };
            _presetBox.Left = 6; _presetBox.Top = 30;
            foreach (Presets.Preset p in Presets.All()) _presetBox.Items.Add(p.Name);
            _presetBox.SelectedIndex = 0;
            var btnApply = new Button { Text = "套用预设", Left = 344, Top = 29, Width = 90, Height = 26 };
            btnApply.Click += (s, e) => ApplyPreset();
            var btnAll = new Button { Text = "全部开启", Left = 442, Top = 29, Width = 80, Height = 26 };
            btnAll.Click += (s, e) => { var c = new GuardConfig(); new Action(() => { })(); ToggleAll(c, true); };
            var btnNone = new Button { Text = "全部关闭", Left = 528, Top = 29, Width = 80, Height = 26 };
            btnNone.Click += (s, e) => { var c = new GuardConfig(); ToggleAll(c, false); };
            _optionCount.Left = 620; _optionCount.Top = 33;
            var desc = new Label { Left = 6, Top = 56, Width = 800, ForeColor = Color.DimGray, AutoSize = true };
            _presetBox.SelectedIndexChanged += (s, e) =>
            {
                desc.Text = Presets.All()[_presetBox.SelectedIndex].Description;
            };
            desc.Text = Presets.All()[0].Description;
            top.Controls.AddRange(new Control[] { lbl, _presetBox, btnApply, btnAll, btnNone, _optionCount, desc });

            _options.Dock = DockStyle.Fill;
            page.Controls.Add(_options);
            page.Controls.Add(top);

            var cfg = new GuardConfig();
            Presets.All()[0].Apply(cfg);
            _options.Refresh(cfg);
            _optionCount.Text = "共 " + _options.FieldCount + " 个开关";
            return page;
        }

        private TabPage TabInstall()
        {
            var page = new TabPage("5. 安装") { Padding = new Padding(14) };
            var lbl = new Label
            {
                Dock = DockStyle.Top, Height = 44, Font = new Font("微软雅黑", 10F),
                Text = "确认无误后点右下角【开始安装】。安装过程约 5~15 秒，完成后桌面会多一个“LabGuard”快捷方式。"
            };
            _startNow.Checked = true;
            var panel = new Panel { Dock = DockStyle.Top, Height = 34 };
            panel.Controls.Add(_startNow);
            var btnInstall = new Button { Text = "开始安装", Width = 130, Height = 34, Dock = DockStyle.Top };
            btnInstall.Click += (s, e) => DoInstall();
            var logBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            logBox.Controls.Add(_progress);
            page.Controls.Add(logBox);
            page.Controls.Add(btnInstall);
            page.Controls.Add(panel);
            page.Controls.Add(lbl);
            return page;
        }

        // ------------------------------------------------------------------ 逻辑
        private void ApplyPreset()
        {
            var cfg = new GuardConfig();
            Presets.All()[Math.Max(0, _presetBox.SelectedIndex)].Apply(cfg);
            _options.Refresh(cfg);
        }

        private void ToggleAll(GuardConfig baseline, bool on)
        {
            foreach (FieldSpec f in SettingsCatalog.All())
            {
                if (f.Kind != FieldKind.Bool) continue;
                if (f.Path.EndsWith(".Enabled", StringComparison.OrdinalIgnoreCase) || f.Path == "Enabled")
                {
                    f.Set(baseline, on);
                }
            }
            // 一些关键子开关也一起切
            if (on)
            {
                Presets.All()[0].Apply(baseline);
            }
            else
            {
                Presets.All()[3].Apply(baseline);
            }
            _options.Refresh(baseline);
        }

        private void Go(int delta)
        {
            int next = _tabs.SelectedIndex + delta;
            if (next < 0) next = 0;
            if (next >= _tabs.TabPages.Count) return;
            if (delta > 0 && !ValidateStep(_tabs.SelectedIndex)) return;
            _tabs.SelectedIndex = next;
            if (next == _tabs.TabPages.Count - 1) _btnNext.Text = "开始安装";
            UpdateButtons();
        }

        private bool ValidateStep(int index)
        {
            if (index == 0 && !_agree.Checked)
            {
                MessageBox.Show("请先勾选“我已阅读并了解”再继续。", "安装向导");
                return false;
            }
            if (index == 2)
            {
                if (_pwd.Text != _pwd2.Text)
                {
                    MessageBox.Show("两次输入的密码不一致，请重新输入。", "安装向导");
                    return false;
                }
                string error = PasswordHasher.Validate(_pwd.Text);
                if (error != null)
                {
                    MessageBox.Show(error, "安装向导");
                    return false;
                }
            }
            return true;
        }

        private void UpdateButtons()
        {
            bool last = _tabs.SelectedIndex == _tabs.TabPages.Count - 1;
            _btnBack.Enabled = _tabs.SelectedIndex > 0 && !_installed;
            _btnNext.Text = last ? "开始安装" : "下一步";
            _btnNext.Enabled = !_installed || !last;
            _btnCancel.Text = _installed ? "关闭" : "取消";
            _status.Text = "第 " + (_tabs.SelectedIndex + 1) + " / " + _tabs.TabPages.Count + " 步";
        }

        private void DoInstall()
        {
            if (!ValidateStep(0) || !ValidateStep(2)) { _tabs.SelectedIndex = _agree.Checked ? 2 : 0; return; }
            string dir = _dirCustom.Checked ? _dir.Text.Trim() : InstallEngine.DefaultInstallDir();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show("请填写安装目录。"); return; }

            var config = new GuardConfig();
            _options.Collect(config);
            config.Enabled = _startNow.Checked && config.Enabled;
            _progress.Clear();
            _btnNext.Enabled = false;
            _btnCancel.Enabled = false;
            Application.DoEvents();

            var engine = new InstallEngine(line =>
            {
                _progress.AppendText(line + Environment.NewLine);
                Application.DoEvents();
            })
            {
                InstallDir = dir,
                Config = config,
                Password = _pwd.Text,
                NoStart = !_startNow.Checked
            };
            try
            {
                engine.Install();
                _installed = true;
                _status.Text = "安装完成";
                if (MessageBox.Show(
                        "安装完成！" + Environment.NewLine + Environment.NewLine +
                        "· 桌面已创建“LabGuard”快捷方式" + Environment.NewLine +
                        "· 托盘小助手会随登录自动运行" + Environment.NewLine +
                        "· 以后要改策略：开始菜单 → 设置（需密码）" + Environment.NewLine + Environment.NewLine +
                        "现在打开设置程序确认一遍策略吗？",
                        "LabGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(Path.Combine(dir, "LabGuard.Settings.exe"))
                        {
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
                Close();
            }
            catch (Exception ex)
            {
                _progress.AppendText("安装失败：" + ex + Environment.NewLine);
                MessageBox.Show("安装失败：" + ex.Message, "LabGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnNext.Enabled = true;
                _btnCancel.Enabled = true;
            }
        }
    }
}
