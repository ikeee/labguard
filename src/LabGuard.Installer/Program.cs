using System;
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
        /// 无人值守 = LabGuard.Installer.exe --silent --password xxx [--install-dir 路径] [--config 预设.json] [--dns IP] [--no-start]
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

            if (string.IsNullOrEmpty(dir)) dir = InstallEngine.DefaultInstallDir();
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
    LabGuard.Installer.exe --silent --password 你的密码123 --install-dir C:\LabGuard ^
        --config ""presets\主机房-普通PC.json"" --dns 192.168.1.10 --no-start

  参数：
    --silent            静默安装（不开向导；需要 --password）
    --dry-run           预演：只打印将执行的步骤，不改系统
            --install-dir <路径>  安装目录（默认 C:\Program Files\LabGuard）
    --password <密码>     小助手密码（6 位及以上字母数字）
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
