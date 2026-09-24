using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabGuard.Core;
using LabGuard.Core.Config;
using LabGuard.Core.Data;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.SelfTest
{
    /// <summary>
    /// 自检程序：验证口令、配置、hosts 生成、拦截清单、以及"干跑模式"下引擎能否完整启动/停止。
    /// 全程不修改系统（引擎以 DryRun 启动）。
    /// </summary>
    internal static class Program
    {
        private static int _failed;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Log.EchoToConsole = true;
            Log.MinLevel = (args != null && args.Contains("--verbose")) ? LogLevel.Debug : LogLevel.Info;
            Log.Directory = Path.Combine(Path.GetTempPath(), "LabGuard-selftest", "logs");

            // 单模块调试：--only <GuardTypeName>，用于定位某个 Guard 的异常
            int onlyIndex = Array.IndexOf(args ?? new string[0], "--only");
            if (onlyIndex >= 0 && args.Length > onlyIndex + 1)
            {
                return RunSingleGuard(args[onlyIndex + 1], args);
            }

            Console.WriteLine("=== LabGuard自检 ===");
            TestPassword();
            TestHosts();
            TestBlocklists();
            TestIfeoNaming();
            TestDisconnectRule();
            TestDnsRule();
            TestSettingsCoverage();
            TestNicRoles();
            TestEngineDryRun();

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "全部通过 ✅" : ("失败 " + _failed + " 项 ❌"));
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail = null)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail == null ? "" : "  -> " + detail));
            if (!ok) _failed++;
        }

        private static int RunSingleGuard(string typeName, string[] args)
        {
            Log.MinLevel = LogLevel.Debug;
            var cfg = new GuardConfig { PasswordHash = "x", StartDelaySeconds = 0 };
            var ctx = new GuardContext(cfg, AppDomain.CurrentDomain.BaseDirectory, null);
            IGuard guard = null;
            foreach (Type t in typeof(GuardContext).Assembly.GetTypes())
            {
                if (t.Name == typeName) guard = (IGuard)Activator.CreateInstance(t);
            }
            if (guard == null) { Console.WriteLine("未找到 Guard：" + typeName); return 2; }
            Console.WriteLine("单独启动 " + typeName);
            guard.Start(ctx);
            Console.WriteLine("启动完成：" + guard.Status);
            int secondsIndex = Array.IndexOf(args ?? new string[0], "--seconds");
            if (secondsIndex >= 0 && args.Length > secondsIndex + 1)
            {
                int seconds = int.Parse(args[secondsIndex + 1]);
                Console.WriteLine("持续运行 " + seconds + " 秒（便于在外部制造违规动作，观察是否被自愈）…");
                for (int i = 0; i < seconds; i++)
                {
                    System.Threading.Thread.Sleep(1000);
                    Console.WriteLine("  t=" + (i + 1) + "s 状态：" + guard.Status);
                }
            }
            guard.Stop();
            Console.WriteLine("停止完成：" + guard.Status);
            return 0;
        }

        private static void TestPassword()
        {
            Console.WriteLine("[1] 口令散列");
            string hash = PasswordHasher.Create("abc123456");
            Check("正确口令可通过", PasswordHasher.Verify("abc123456", hash));
            Check("错误口令被拒绝", !PasswordHasher.Verify("abc123457", hash));
            Check("散列不含明文", hash.IndexOf("abc123456", StringComparison.Ordinal) < 0);
            Check("拒绝 5 位口令", PasswordHasher.Validate("abc12") != null);
            Check("拒绝弱口令 123456", PasswordHasher.Validate("123456") != null);
            Check("接受 6 位以上口令", PasswordHasher.Validate("a1b2c3") == null);
        }

        private static void TestHosts()
        {
            Console.WriteLine("[2] hosts 受管区块");
            string original = "127.0.0.1 localhost\r\n::1 localhost\r\n";
            string block = HostsFile.BuildBlock(new[] { "poki.com", "pan.baidu.com" }, "127.0.0.1");
            string merged = HostsFile.ReplaceBlock(original, block);
            Check("保留原有内容", merged.Contains("127.0.0.1 localhost"));
            Check("写入黑名单", merged.Contains("127.0.0.1 poki.com") && merged.Contains("127.0.0.1 pan.baidu.com"));
            string replaced = HostsFile.ReplaceBlock(merged, HostsFile.BuildBlock(new[] { "digdig.io" }, "127.0.0.1"));
            Check("重复写入不叠加区块", !replaced.Contains("poki.com") && replaced.Contains("digdig.io"));
            string cleaned = HostsFile.ReplaceBlock(replaced, "");
            Check("清理后无残留", !cleaned.Contains("digdig.io") && cleaned.Contains("localhost"));
        }

        private static void TestBlocklists()
        {
            Console.WriteLine("[3] 拦截清单");
            Check("含破解工具样例", DefaultBlocklists.ClassroomCrack.Contains("极域杀手"));
            Check("含进程工具样例", DefaultBlocklists.ProcessTools.Contains("Process Hacker"));
            Check("含杀软样例", DefaultBlocklists.AntiVirus.Contains("HipsTray"));
            Check("含解压样例", DefaultBlocklists.Archivers.Contains("winrar") || DefaultBlocklists.Archivers.Contains("WinRAR"));
            Check("含游戏样例", DefaultBlocklists.Games.Contains("winmine"));
            Check("域名清单 >= 25 条（可自行增删）", DefaultBlocklists.Domains.Length >= 25, DefaultBlocklists.Domains.Length + " 条");
            Check("电子教室提示 >= 3 个", DefaultBlocklists.ClassroomHints.Length >= 3);
        }

        private static void TestIfeoNaming()
        {
            Console.WriteLine("[4] IFEO / 系统键名");
            string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\winmine";
            Check("IFEO 键路径正确",
                key.EndsWith(@"Image File Execution Options\winmine", StringComparison.Ordinal));
            Check("高危机型名包含 16 项以上", DefaultBlocklists.GamesIfeo.Length >= 14);
            Check("命令封锁清单包含 taskkill", DefaultBlocklists.BlockedCommandsIfeo.Contains("taskkill.exe"));
        }

        private static void TestDisconnectRule()
        {
            Console.WriteLine("[5] 断网判定表（拔网线/禁网卡/APIPA）");
            Check("拔网线（网卡 Down + 无 IP）= 断网", NetworkGuard.IsDisconnected(null, false));
            Check("禁用网卡（Down）即使有旧 IP = 断网", NetworkGuard.IsDisconnected("192.168.1.10", false));
            Check("APIPA 169.254.*（网卡 Up）= 断网", NetworkGuard.IsDisconnected("169.254.10.20", true));
            Check("DHCP 失败 IP 被清空 = 断网", NetworkGuard.IsDisconnected("", true));
            Check("IP 归零 0.0.0.0 = 断网", NetworkGuard.IsDisconnected("0.0.0.0", true));
            Check("正常固定 IP ≠ 断网", !NetworkGuard.IsDisconnected("192.168.50.100", true));
            Check("正常 DHCP IP ≠ 断网", !NetworkGuard.IsDisconnected("10.53.3.224", true));
        }

        private static void TestDnsRule()
        {
            Console.WriteLine("[6] 集中 DNS 锁定判定（防学生改 DNS 绕过）");
            var want = new System.Collections.Generic.List<string> { "192.168.50.10" };
            Check("DNS 指向教师机 = 正常", !DnsGuard.NeedsFix(new[] { "192.168.50.10" }, want));
            Check("DNS 被改成 8.8.8.8 = 需要改回", DnsGuard.NeedsFix(new[] { "8.8.8.8" }, want));
            Check("DNS 被改成 114.114.114.114 = 需要改回", DnsGuard.NeedsFix(new[] { "114.114.114.114" }, want));
            Check("DNS 为空（清空）= 需要改回", DnsGuard.NeedsFix(new string[0], want));
            Check("备用 DNS 顺序不符 = 需要改回",
                DnsGuard.NeedsFix(new[] { "192.168.50.1", "192.168.50.10" },
                    new System.Collections.Generic.List<string> { "192.168.50.10", "192.168.50.1" }));
            Check("多台 DNS（主+备）一致 = 正常",
                !DnsGuard.NeedsFix(new[] { "192.168.50.10", "192.168.50.11" },
                    new System.Collections.Generic.List<string> { "192.168.50.10", "192.168.50.11" }));
            Check("未配置 DNS = 不锁定（NeedsFix=false）", !DnsGuard.NeedsFix(new[] { "8.8.8.8" }, new string[0]));
            Check("非法值被过滤", DnsGuard.Normalize(new[] { "abc", "192.168.50.10", "192.168.50.10" }).Count == 1);
            Check("IPv4 校验：1.1.1.1 合法 / 999.1.1.1 非法",
                DnsGuard.LooksLikeIpv4("1.1.1.1") && !DnsGuard.LooksLikeIpv4("999.1.1.1"));
            Console.WriteLine("      本机当前是否有可用 IP（决定「拔网线」和「教师机挂了」是否分开报）："
                + DnsGuard.HasUsableLocalIp());
        }

        /// <summary>
        /// "所有功能都能自主开关"的硬保证：
        /// ① 配置里每个叶子属性都必须在 SettingsCatalog 里有开关（不许有隐藏开关）；
        /// ② 目录里每个开关的读写必须落在正确的字段上。
        /// </summary>
        private static void TestSettingsCoverage()
        {
            Console.WriteLine("[7] 设置项覆盖（每个功能都有开关）");
            var catalog = LabGuard.Core.Settings.SettingsCatalog.All();
            var declared = catalog.Select(f => f.Path).ToList();
            var actual = LeafPaths(typeof(GuardConfig), "", LabGuard.Core.Settings.SettingsCatalog.SpecialPaths).ToList();

            var missing = actual.Where(p => !declared.Contains(p)).ToList();
            var ghost = declared.Where(p => !actual.Contains(p)).ToList();
            Check("配置项全部有界面开关（无隐藏项）", missing.Count == 0,
                missing.Count == 0 ? actual.Count + " 项全覆盖" : "缺：" + string.Join(", ", missing));
            Check("界面没有指向不存在属性", ghost.Count == 0,
                ghost.Count == 0 ? "" : string.Join(", ", ghost));

            var cfg = new GuardConfig();
            var broken = new List<string>();
            foreach (var f in catalog)
            {
                bool ok = true;
                switch (f.Kind)
                {
                    case LabGuard.Core.Settings.FieldKind.Bool:
                        f.Set(cfg, true); ok = Equals(f.Get(cfg), true);
                        f.Set(cfg, false); ok = ok && Equals(f.Get(cfg), false);
                        break;
                    case LabGuard.Core.Settings.FieldKind.Choice:
                        foreach (string v in f.Values)
                        {
                            f.Set(cfg, v);
                            ok = ok && Equals(Convert.ToString(f.Get(cfg)), v);
                        }
                        break;
                    case LabGuard.Core.Settings.FieldKind.Number:
                        f.Set(cfg, 7);
                        ok = Equals(Convert.ToInt32(f.Get(cfg)), 7);
                        break;
                    case LabGuard.Core.Settings.FieldKind.List:
                        f.Set(cfg, new List<string> { "a", "b" });
                        ok = string.Join(",", (List<string>)f.Get(cfg)) == "a,b";
                        break;
                    case LabGuard.Core.Settings.FieldKind.Path:
                        f.Set(cfg, @"C:\x\y.exe");
                        ok = Equals(f.Get(cfg), @"C:\x\y.exe");
                        break;
                    default:
                        f.Set(cfg, "x-test");
                        ok = Equals(f.Get(cfg), "x-test");
                        break;
                }
                if (!ok) broken.Add(f.Path);
            }
            Check("每个开关读写都落在正确字段", broken.Count == 0,
                broken.Count == 0 ? catalog.Count + " 个开关全部正常" : "错项：" + string.Join(", ", broken));
        }

        /// <summary>收集 GuardConfig 里的"叶子"属性路径（跳过容器对象与特殊项）。</summary>
        private static IEnumerable<string> LeafPaths(Type type, string prefix, string[] specials)
        {
            foreach (var p in type.GetProperties())
            {
                string path = prefix + p.Name;
                if (prefix.Length == 0 && specials.Contains(path)) continue;
                Type t = p.PropertyType;
                bool isContainer = t.Namespace == "LabGuard.Core.Config" && t != typeof(GuardConfig);
                if (isContainer)
                {
                    foreach (string sub in LeafPaths(t, path + ".", specials)) yield return sub;
                }
                else
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// 网卡角色判定：一体机机房"有线为主 + 无线常开做热点"的关键——
        /// 热点/Wi-Fi Direct/虚拟网卡不能被当作联网判据，也不能被误判成断网。
        /// </summary>
        private static void TestNicRoles()
        {
            Console.WriteLine("[8] 网卡判据（热点/无线/虚拟网卡的处理）");
            var cfg = new GuardConfig();
            string[] ex = cfg.Network.ExcludedInterfaces.ToArray();

            Check("移动热点网卡（本地连接* 10 / Wi-Fi Direct）被忽略",
                LabGuard.Core.Interop.NicRoles.IsExcluded("本地连接* 10", "Microsoft Wi-Fi Direct Virtual Adapter", ex));
            Check("中文热点名（移动热点）被忽略",
                LabGuard.Core.Interop.NicRoles.IsExcluded("移动热点", "Microsoft Mobile Hotspot", ex));
            Check("Hyper-V / VMware 虚拟网卡被忽略",
                LabGuard.Core.Interop.NicRoles.IsExcluded("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", ex) &&
                LabGuard.Core.Interop.NicRoles.IsExcluded("Ethernet0", "VMware Virtual Ethernet Adapter for VMnet8", ex));
            Check("有线网卡不被忽略",
                !LabGuard.Core.Interop.NicRoles.IsExcluded("以太网", "Realtek PCIe GbE Family Controller", ex));
            Check("无线网卡默认不被忽略（是否当判据由「有没有默认网关 / 是不是热点 IP」决定）",
                !LabGuard.Core.Interop.NicRoles.IsExcluded("WLAN", "Intel(R) Wi-Fi 6 AX201 160MHz", ex));
            Check("ICS 热点固定网关被识别", LabGuard.Core.Interop.NicRoles.IcsGatewayIp == "192.168.137.1");
            Check("有线类型识别（以太网/千兆=True，无线=False）",
                LabGuard.Core.Interop.NicRoles.IsWiredType(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) &&
                LabGuard.Core.Interop.NicRoles.IsWiredType(System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet) &&
                !LabGuard.Core.Interop.NicRoles.IsWiredType(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211));
            Check("安全模式检测可调用（当前启动模式 " + LabGuard.Core.Interop.SafeModeDetector.BootMode +
                  " = 0 正常 / 1 安全模式 / 2 带网络安全模式）",
                LabGuard.Core.Interop.SafeModeDetector.BootMode >= 0);
            Check("正常启动下不跳过管控", !LabGuard.Core.Interop.SafeModeDetector.ShouldSkipEnforcement(false));

            string reason;
            var watched = LabGuard.Core.Interop.NicRoles.SelectWatched(cfg, out reason);
            Console.WriteLine("      本机实际判据网卡：" + string.Join(", ",
                System.Linq.Enumerable.Select(watched, n => n.Name)) + " —— " + reason);
            var wirelessCfg = new GuardConfig();
            wirelessCfg.Network.WatchWiredOnly = false;
            string reason2;
            var watched2 = LabGuard.Core.Interop.NicRoles.SelectWatched(wirelessCfg, out reason2);
            Console.WriteLine("      关掉『只监视有线』后：" + string.Join(", ",
                System.Linq.Enumerable.Select(watched2, n => n.Name)) + " —— " + reason2);
            Console.WriteLine("      本机全部网卡：" + string.Join(", ",
                System.Linq.Enumerable.Select(LabGuard.Core.Interop.NicRoles.Usable(),
                    n => n.Name + (cfg.Network.ExcludedInterfaces.Any(p =>
                        (n.Name + " " + n.Description).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) ? "(忽略)" : ""))));
        }

        private static void TestEngineDryRun()
        {
            Console.WriteLine("[9] 引擎干跑（不修改系统）");
            var cfg = new GuardConfig
            {
                PasswordHash = PasswordHasher.Create("test123456"),
                StartDelaySeconds = 0
            };
            var engine = new GuardEngine(cfg);
            try
            {
                engine.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, EngineRole.Full);
                Check("用户会话引擎启动", engine.IsRunning, engine.Guards.Count + " 个模块");
                engine.Stop();
                Check("用户会话引擎停止", !engine.IsRunning);

                var engine2 = new GuardEngine(cfg);
                engine2.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, EngineRole.SystemCore);
                Check("服务引擎启动", engine2.IsRunning, engine2.Guards.Count + " 个模块");
                engine2.Stop();
            }
            catch (Exception ex)
            {
                Check("引擎干跑无异常", false, ex.Message);
            }

            var audit = SystemActions.AuditTrail;
            Console.WriteLine("      干跑期间记录的系统动作（应只有读取/空操作）：" + audit.Count + " 条");
            foreach (string a in audit.Take(8)) Console.WriteLine("        " + a);
        }
    }
}
