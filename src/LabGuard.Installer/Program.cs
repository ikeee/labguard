using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Config;

namespace LabGuard.Installer
{
    internal static class Program
    {
        /// <summary>
        /// 单文件安装包：本程序引用的 LabGuard.Core.dll 不打在磁盘上，而是从内嵌包体里加载。
        /// （否则"一个 exe"必须旁边再放一个 dll，就称不上一键了。）
        /// </summary>
        static Program()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                try
                {
                    string simpleName = new System.Reflection.AssemblyName(e.Name).Name;
                    if (!string.Equals(simpleName, "LabGuard.Core", StringComparison.OrdinalIgnoreCase)) return null;

                    // 优先用同目录的 dll（方便排障），否则用内嵌包体
                    string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabGuard.Core.dll");
                    if (File.Exists(beside)) return System.Reflection.Assembly.LoadFrom(beside);

                    System.Reflection.Assembly self = System.Reflection.Assembly.GetExecutingAssembly();
                    foreach (string res in self.GetManifestResourceNames())
                    {
                        if (res.EndsWith("payload/LabGuard.Core.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            using (Stream s = self.GetManifestResourceStream(res))
                            {
                                byte[] bytes = new byte[s.Length];
                                s.Read(bytes, 0, bytes.Length);
                                return System.Reflection.Assembly.Load(bytes);
                            }
                        }
                    }
                }
                catch { }
                return null;
            };
        }

        /// <summary>
        /// LabGuard· 一键安装程序
        /// 双击 = 图形向导（欢迎 → 安装位置 → 密码 → 80 项开关 → 安装）
        /// 无人值守 = LabGuard.Installer.exe --silent --password xxx [--install-dir 路径] [--config 预设.json] [--dns IP] [--pair 配对文件.json] [--no-start]
        /// 预演     = 追加 --dry-run（只打印步骤，不改系统）
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            if (Has(args, "--help") || Has(args, "-h"))
            {
                Console.WriteLine(Usage());
                return;
            }

            // --screenshot <目录>：把向导各页面渲染成 PNG（不安装、不改系统），供文档与人工核对
            int shotIndex = Array.IndexOf(args ?? new string[0], "--screenshot");
            if (shotIndex >= 0)
            {
                string shotDir = args.Length > shotIndex + 1
                    ? args[shotIndex + 1]
                    : Path.Combine(Path.GetTempPath(), "labguard-wizard");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var shotForm = new InstallerForm())
                {
                    LabGuard.Core.UI.UiCapture.ShowOffScreen(shotForm);
                    foreach (string f in shotForm.RenderScreenshots(shotDir)) Console.WriteLine(f);
                    shotForm.Hide();
                    Console.WriteLine("向导截图目录：" + shotDir);
                }
                return;
            }

            bool silent = Has(args, "--silent") || Has(args, "--dry-run");
            if (!silent)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm());
                return;
            }

            // ---------------- 无人值守 / 预演 ----------------
            bool dryRun = Has(args, "--dry-run");
            bool noStart = Has(args, "--no-start");
            string password = Value(args, "--password");
            string dir = Value(args, "--install-dir");
            string configJson = Value(args, "--config");
            string dns = Value(args, "--dns");
            string classroom = Value(args, "--classroom");

            if (string.IsNullOrEmpty(dir))
            {
                dir = Has(args, "--disguise-dir")
                    ? (InstallEngine.DisguisedInstallDir() ?? InstallEngine.DefaultInstallDir())
                    : InstallEngine.DefaultInstallDir();
            }
            GuardConfig config;
            if (!string.IsNullOrEmpty(configJson) && File.Exists(configJson))
            {
                config = ConfigStore.ImportFrom(configJson);
                Console.WriteLine("已套用预设：" + configJson);
            }
            else
            {
                config = new GuardConfig();
                LabGuard.Core.Settings.Presets.All()[0].Apply(config);
            }
            if (!string.IsNullOrEmpty(classroom))
            {
                config.Classroom.MainExecutable = classroom;
                Console.WriteLine("已指定课堂软件学生端：" + classroom);
            }
            else if (string.IsNullOrEmpty(config.Classroom.MainExecutable))
            {
                string reason;
                var best = LabGuard.Core.Interop.ClassroomDetector.DetectBest(out reason);
                if (best != null)
                {
                    config.Classroom.MainExecutable = best.Path;
                    Console.WriteLine("自动识别的课堂软件：" + reason);
                }
                else
                {
                    Console.WriteLine("未识别到课堂软件（可在装好后到【设置】→ 第 1 组手动填写）");
                }
            }
            if (!string.IsNullOrEmpty(dns))
            {
                config.Network.DnsServers.Clear();
                foreach (string one in dns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (LabGuard.Core.Guards.DnsGuard.LooksLikeIpv4(one)) config.Network.DnsServers.Add(one.Trim());
                }
                config.Network.EnforceDns = config.Network.DnsServers.Count > 0;
            }

            var engine = new InstallEngine(s =>
            {
                Console.WriteLine(s);
            })
            {
                DryRun = dryRun,
                NoStart = noStart,
                InstallDir = dir,
                Config = config,
                Password = password
            };
            try
            {
                engine.Install();
                // 服务器版：装完可直接导入教师机的配对文件（--pair <教师机导出的 pairing.json>），
                // 一次性完成"安装 + 配对"，机房批量部署不用再逐台点设置面板。
                string pairFile = Value(args, "--pair");
                if (!string.IsNullOrEmpty(pairFile))
                {
                    if (dryRun)
                    {
                        Console.WriteLine("（预演）将导入配对文件：" + pairFile);
                    }
                    else
                    {
                        string settingsExe = Path.Combine(engine.InstallDir, "LabGuard.Settings.exe");
                        if (!File.Exists(pairFile)) { Console.Error.WriteLine("配对失败：找不到配对文件 " + pairFile); Environment.ExitCode = 1; }
                        else if (!File.Exists(settingsExe)) { Console.Error.WriteLine("配对失败：找不到 " + settingsExe); Environment.ExitCode = 1; }
                        else
                        {
                            var psi = new ProcessStartInfo(settingsExe, "--pair \"" + pairFile + "\"")
                            {
                                UseShellExecute = false,
                                WorkingDirectory = engine.InstallDir,
                            };
                            using (var p = Process.Start(psi))
                            {
                                p.WaitForExit(60000);
                                if (p.ExitCode == 0) Console.WriteLine("已导入配对文件：本机将接受教师机统一下发的策略（≤10 秒内生效）。");
                                else { Console.Error.WriteLine("配对失败（退出码 " + p.ExitCode + "）"); Environment.ExitCode = 1; }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("安装失败：" + ex);
                Environment.ExitCode = 1;
            }
        }

        private static string Usage()
        {
            return @"LabGuard安装程序

  双击运行            = 图形向导：欢迎 → 安装位置 → 密码 → 功能开关(90+项) → 安装

  无人值守示例：
    LabGuard.Installer.exe --silent --password 你的密码123 --install-dir C:\LabGuard --pair D:\pairing.json ^
        --config ""presets\主机房-普通PC.json"" --dns 192.168.1.10 --no-start

  参数：
    --silent            静默安装（不开向导；需要 --password）
    --dry-run           预演：只打印将执行的步骤，不改系统
            --install-dir <路径>  安装目录（默认 C:\Program Files\LabGuard）
    --disguise-dir      安装目录名不显眼（自动生成，如 C:\Program Files\f738291；未指定 --install-dir 时生效）
    --password <密码>     小助手密码（6 位及以上字母数字）
  --pair <文件>         服务器版：导入教师机导出的配对文件（装完即接受统一管控）
    --config <json>     套用预设配置（presets\ 目录下自带两份）
    --dns <IP>          集中 DNS（可选；填了才做 DNS 锁定）
    --classroom <exe>   课堂软件学生端路径（留空则自动识别，识别不到可在设置里填）
    --no-start          装好但先不启动（策略暂不生效，之后在设置里启用）
";
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string a in args ?? new string[0])
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Value(string[] args, string name)
        {
            for (int i = 0; i < (args?.Length ?? 0) - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
