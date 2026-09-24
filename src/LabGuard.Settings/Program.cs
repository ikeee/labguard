using System;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;
using LabGuard.Core.UI;

namespace LabGuard.Settings
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Log.MinLevel = LogLevel.Info;
            GuardConfig config = ConfigStore.Load();

            // 界面自检：不显示窗口，只构建一次并统计控件，验证"每个配置项都渲染出控件"
            if (Array.IndexOf(args ?? new string[0], "--selftest-ui") >= 0)
            {
                if (string.IsNullOrEmpty(config.PasswordHash)) config.PasswordHash = PasswordHasher.Create("a1b2c3");
                using (var form = new SettingsForm(config))
                {
                    var counts = new System.Collections.Generic.Dictionary<string, int>();
                    CountControls(form, counts);
                    int fields = LabGuard.Core.Settings.SettingsCatalog.All().Count;
                    int sections = new System.Collections.Generic.HashSet<string>(
                        System.Linq.Enumerable.Select(LabGuard.Core.Settings.SettingsCatalog.All(), f => f.Section)).Count;
                    Console.WriteLine("设置界面构建成功。");
                    Console.WriteLine("分组数：" + sections);
                    Console.WriteLine("配置项（开关）：" + fields);
                    Console.WriteLine("渲染控件统计：" + string.Join("，", counts));
                    return;
                }
            }

            // 静默初始化：LabGuard.Settings.exe --init <密码>  （批量部署用，不弹界面）
            int initIndex = Array.IndexOf(args ?? new string[0], "--init");
            if (initIndex >= 0 && args.Length > initIndex + 1)
            {
                string pwd = args[initIndex + 1];
                // 已设置过密码的机器，改密码必须提供旧密码（否则等于人人可重置口令的免密后门）
                if (!string.IsNullOrEmpty(config.PasswordHash))
                {
                    string oldPwd = null;
                    int oldIndex = Array.IndexOf(args, "--old");
                    if (oldIndex >= 0 && args.Length > oldIndex + 1) oldPwd = args[oldIndex + 1];
                    if (string.IsNullOrEmpty(oldPwd) || !PasswordHasher.Verify(oldPwd, config.PasswordHash))
                    {
                        Console.Error.WriteLine("本机已设置过密码：改用 --init <新密码> --old <当前密码>。");
                        Echo("拒绝重置密码：未提供正确的当前密码（这是有意设计，防止免密重置）。");
                        FlushReport("deploy-init-rejected");
                        Environment.ExitCode = 3;
                        return;
                    }
                }
                string error = PasswordHasher.Validate(pwd);
                if (error != null)
                {
                    Console.Error.WriteLine(error);
                    Environment.ExitCode = 2;
                    return;
                }
                // 可选：--import <预设.json> 套用预设；--dns <教师机IP> 顺便填集中 DNS
                int importIndex = Array.IndexOf(args, "--import");
                if (importIndex >= 0 && args.Length > importIndex + 1)
                {
                    config = ConfigStore.ImportFrom(args[importIndex + 1]);
                    Echo("已套用预设：" + args[importIndex + 1]);
                }
                config.PasswordHash = PasswordHasher.Create(pwd);
                int dnsIndex = Array.IndexOf(args, "--dns");
                if (dnsIndex >= 0 && args.Length > dnsIndex + 1)
                {
                    config.Network.DnsServers.Clear();
                    foreach (string one in args[dnsIndex + 1].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (LabGuard.Core.Guards.DnsGuard.LooksLikeIpv4(one)) config.Network.DnsServers.Add(one.Trim());
                    }
                    config.Network.EnforceDns = config.Network.DnsServers.Count > 0;
                    Echo("已设置集中 DNS：" + string.Join(",", config.Network.DnsServers));
                }
                ConfigStore.Save(config);
                LabGuard.Core.Guards.WatchdogGuard.MarkPaused(false);
                Echo("已初始化配置：密码已设置，策略已应用。");
                Echo("配置目录：" + ConfigStore.DataDir);
                FlushReport("deploy-init");
                return;
            }

            // 导出预设：LabGuard.Settings.exe --export-preset <主机房|一体机机房> <输出.json>
            int presetIndex = Array.IndexOf(args ?? new string[0], "--export-preset");
            if (presetIndex >= 0 && args.Length > presetIndex + 2)
            {
                string name = args[presetIndex + 1];
                string outPath = args[presetIndex + 2];
                GuardConfig preset = BuildPreset(name);
                System.IO.File.WriteAllText(outPath, ConfigStore.Serialize(preset),
                    new System.Text.UTF8Encoding(false));
                Console.WriteLine("已导出预设【" + name + "】-> " + outPath);
                return;
            }

            // 查看当前策略：LabGuard.Settings.exe --show-config （部署后逐台核对用）
            if (Array.IndexOf(args ?? new string[0], "--show-config") >= 0)
            {
            Echo("=== " + LabGuard.Core.AppInfo.ProductName + " v" + LabGuard.Core.AppInfo.Version +
                 "（" + ConfigStore.DataDir + "）===");
            if (ConfigStore.RestoredFromMirror)
                Echo("⚠ 配置来源：从注册表备份恢复（配置文件被删/被破坏过）");
            if (ConfigStore.TamperSuspected)
                Echo("⚠ 配置来源：配置文件与备份都缺失（已安装标记还在，疑似被人破坏）");
            Echo("已安装标记 Installed = " + ConfigStore.IsInstalled());
                Echo("密码已设置            = " + (!string.IsNullOrEmpty(config.PasswordHash)));
                Echo("监控总开关 Enabled    = " + config.Enabled);
                Echo("开机延迟检测（秒）    = " + config.StartDelaySeconds);
                Echo("电子教室保护          = " + config.Classroom.Enabled +
                                  "（路径：" + (string.IsNullOrEmpty(config.Classroom.MainExecutable) ? "自动探测" : config.Classroom.MainExecutable) + "）");
                Echo("断网检测 / 遮罩 / 响鸣 = " + config.Network.DetectDisconnected + " / " +
                                  config.Network.DisconnectMask + "（" + config.Network.DisconnectMaskBackground + "） / " +
                                  (config.Network.DisconnectSoundAfterSeconds <= 0
                                      ? "关"
                                      : config.Network.DisconnectSoundAfterSeconds + "秒×" + config.Network.DisconnectSoundTimes + "次"));
                Echo("只监视有线网卡        = " + config.Network.WatchWiredOnly +
                                  "（全部断开才算断网=" + config.Network.DisconnectRequireAllDown + "）");
                Echo("集中 DNS 锁定         = " + config.Network.EnforceDns + " -> " +
                                  (config.Network.DnsServers.Count == 0 ? "（未填地址=不锁定）" : string.Join(",", config.Network.DnsServers)) +
                                  "；关闭 DoH=" + config.Network.BlockDoh +
                                  "；只锁判据网卡=" + config.Network.DnsLockOnlyWatchedInterfaces);
                Echo("hosts 黑名单 / 保护   = " + config.Hosts.Enabled + " / " + config.Hosts.ProtectFileAcl);
                Echo("USB 允许存储设备      = " + config.Usb.AllowStorage);
                Echo("新文件监控            = " + config.FileCreation.Enabled + "（模式 " + config.FileCreation.Mode +
                                  "，动作 " + config.FileCreation.Action + "）");
                Echo("违规软件处置          = " + config.ProcessBlock.Action);
                Echo("任务栏/Win键/任务管理器 = " + config.Shell.HideTaskView + " / " +
                                  config.Shell.BlockWindowsKey + " / " + config.Shell.DisableTaskManager);
                Echo("安全模式限制          = " + config.SafeMode.BlockNetworkSafeMode);
                Echo("壁纸 / 机器编号       = " + config.Wallpaper.Enabled + " / " + config.Wallpaper.ShowMachineNumber +
                                  "（后 " + config.Wallpaper.MachineNumberChars + " 位）");
            Echo("互相守护 / 服务异常重启电脑 = " + config.Watchdog.Enabled + " / " + config.Watchdog.RebootOnServiceFailure);
            Echo("文件自愈 / 心跳上报 = " + config.Watchdog.RestoreMissingFiles + " / " +
                 (string.IsNullOrWhiteSpace(config.Watchdog.ReportUrl) ? "未配置" : config.Watchdog.ReportUrl));
                FlushReport("deploy-show-config");
                return;
            }

            // 已设置密码则必须先通过密码才能改设置
            if (!string.IsNullOrEmpty(config.PasswordHash))
            {
                using (var dlg = new PasswordDialog("LabGuard - 设置", "输入小助手密码：", config.PasswordHash))
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                    {
                        MessageBox.Show("密码不正确，不能修改设置。", "LabGuard",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SettingsForm(config));
        }

        private static void CountControls(Control control, System.Collections.Generic.Dictionary<string, int> counts)
        {
            string name = control.GetType().Name;
            if (name == "CheckBox" || name == "ComboBox" || name == "NumericUpDown" || name == "TextBox" || name == "Button")
            {
                int n;
                counts.TryGetValue(name, out n);
                counts[name] = n + 1;
            }
            foreach (Control child in control.Controls) CountControls(child, counts);
        }

        /// <summary>
        /// 控制台输出 + 同时落一份部署日志。
        /// （本程序是 WinExe，某些 shell 下 stdout 抓不到，落文件才能保证老师能回看结果。）
        /// </summary>
        private static readonly System.Collections.Generic.List<string> Report =
            new System.Collections.Generic.List<string>();

        private static void Echo(string line)
        {
            Console.WriteLine(line);
            Report.Add(line);
        }

        private static void FlushReport(string name)
        {
            try
            {
                string dir = Path.Combine(ConfigStore.DataDir, "logs");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"),
                    Report, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 机房预设：把"某一类机房"的合理默认值固化下来，便于整机房导入。
        /// 注意：集中 DNS 的服务器地址必须按机房填（预设里留空），用 --dns 参数或导入后在设置里补。
        /// </summary>
        private static GuardConfig BuildPreset(string name)
        {
            var c = new GuardConfig();
            // 两类机房共同的基础策略
            c.Classroom.Enabled = true;
            c.Network.DetectDisconnected = true;
            c.Network.DisconnectMask = true;
            c.Network.DisconnectMaskBackground = "Wallpaper";
            c.Network.DisconnectSoundAfterSeconds = 60;
            c.Network.DisconnectSoundTimes = 3;
            c.Network.LockOnViolation = false;
            c.Network.ShutdownOnViolation = false;
            c.Network.EnforceDns = true;          // 地址留空 → 装完在设置里填教师机 IP（或用 --dns）
            c.Network.BlockDoh = true;
            c.Hosts.Enabled = false;              // 有集中 DNS（AdGuard）时不需要 hosts 黑名单
            c.Hosts.ProtectFileAcl = true;        // 但仍保护 hosts，防本地写映射
            c.ProcessBlock.Action = "Kill";
            c.FileCreation.Mode = "CppOnly";
            c.Usb.AllowStorage = false;
            c.Shell.BlockWindowsKey = true;
            c.Shell.HideTaskView = true;
            c.Shell.DisableTaskManager = true;
            c.Shell.DisableRegistryTools = true;
            c.Wallpaper.ShowMachineNumber = true;
            c.Watchdog.Enabled = true;
            c.Watchdog.RebootOnServiceFailure = false;   // 默认不重启电脑（只重启服务）

            if (name.Contains("一体机"))
            {
                // 一体机机房：有线为主 + 无线常开做热点（STEAM 课）
                c.Network.WatchWiredOnly = true;              // 只把有线当判据
                c.Network.DisconnectRequireAllDown = true;
                c.Network.DnsLockOnlyWatchedInterfaces = true;
                foreach (string extra in new[] { "WLAN", "无线", "Wi-Fi" })
                {
                    if (!c.Network.ExcludedInterfaces.Contains(extra)) c.Network.ExcludedInterfaces.Add(extra);
                }
            }
            else
            {
                // 普通 PC 机房：有线为主，但若无线也参与联网则由"全部断开"决定
                c.Network.WatchWiredOnly = true;
                c.Network.DisconnectRequireAllDown = true;
            }
            return c;
        }
    }
}
