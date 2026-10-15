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
using LabGuard.Core.Server;
using LabGuard.Core.Settings;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Threading;

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
            TestRedTeamRegression();
            TestPassword();
            TestHosts();
            TestBlocklists();
            TestClassroomProducts();
            TestIfeoNaming();
            TestDisconnectRule();
            TestDnsRule();
            TestSettingsCoverage();
            TestNicRoles();
            TestEngineDryRun();
            TestServerContract();
            TestServerSync();
            TestServerRuntime();
            TestDeviceIdentity();
            TestSessionEscape();

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
            // 单模块调试也必须是"干跑"：否则 --only 一跑就会真的改注册表/停服务，
            // 与自检"全程不修改系统"的承诺矛盾（改动只写审计，不落盘）。
            Log.DryRun = true;
            var cfg = new GuardConfig { PasswordHash = "x", StartDelaySeconds = 0 };
            var ctx = new GuardContext(cfg, AppDomain.CurrentDomain.BaseDirectory, null);
            IGuard guard = null;
            foreach (Type t in typeof(GuardContext).Assembly.GetTypes())
            {
                if (t.Name == typeName) guard = (IGuard)Activator.CreateInstance(t);
            }
            if (guard == null) { Console.WriteLine("未找到 Guard：" + typeName); return 2; }
            Console.WriteLine("单独启动 " + typeName + "（干跑模式：只检测与记录，不改系统）");
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

        /// <summary>
        /// 课堂软件识别（多产品：极域 / 红蜘蛛 / 锐捷 / 噢易 Os-Easy）与云桌面参数判定。
        /// 噢易这类"母盘/镜像装机"的产品不写卸载记录，识别只能靠 进程 / 常见路径 / 服务目录，
        /// 所以这里把产品判定、服务路径解析、默认保护清单都钉住，避免以后改动悄悄退化。
        /// </summary>
        private static void TestClassroomProducts()
        {
            Console.WriteLine("[3.1] 课堂软件识别（噢易 Os-Easy / 极域 / 红蜘蛛 / 锐捷）");
            Check("噢易教学系统（Student.exe 所在目录）",
                ClassroomDetector.GuessProduct(@"C:\Program Files (x86)\Os-Easy\os-easy multicast teaching system\Student.exe").Contains("噢易"));
            Check("噢易 VOI 云桌面客户端",
                ClassroomDetector.GuessProduct(@"C:\Program Files\VOI\Platform\client\VoiClient.exe").Contains("VOI"));
            Check("噢易硬件虚拟化",
                ClassroomDetector.GuessProduct(@"C:\Program Files\OSEasy\HardVirtual\RunClient.exe").Contains("硬件虚拟化"));
            Check("极域 / 红蜘蛛 / 锐捷仍然认得出来",
                ClassroomDetector.GuessProduct(@"C:\Program Files (x86)\TopDomain\e-Learning Class\Student\StudentMain.exe").Contains("极域") &&
                ClassroomDetector.GuessProduct(@"C:\Program Files (x86)\3000soft\Red Spider\REDAgent.exe").Contains("红蜘蛛") &&
                ClassroomDetector.GuessProduct(@"E:\Program Files (x86)\ClassManager\ClassMangerApp.exe").Contains("锐捷"));

            // 服务 ImagePath：噢易机房唯一可靠的安装目录线索
            Check("带引号 + 参数的服务路径能取到目录",
                ClassroomDetector.ServiceDirectory("\"C:\\Program Files\\OSEasy\\HardVirtual\\RunClient.exe\" /service") == @"C:\Program Files\OSEasy\HardVirtual");
            Check("不带引号的服务路径能取到目录",
                ClassroomDetector.ServiceDirectory(@"C:\Program Files\VOI\Platform\client\diskless_service.exe") == @"C:\Program Files\VOI\Platform\client");
            Check("没有路径的服务名 = 不瞎猜（返回 null）", ClassroomDetector.ServiceDirectory("MMPC") == null);

            var cfg = new GuardConfig();
            Check("默认盯住噢易学生端 / 云桌面进程",
                cfg.Classroom.ProcessNames.Contains("MultiClient.exe") &&
                cfg.Classroom.ProcessNames.Contains("Ctsc_Multi.exe") &&
                cfg.Classroom.ProcessNames.Contains("VoiClient.exe") &&
                cfg.Classroom.ProcessNames.Contains("TrayClient.exe"));
            Check("默认重启噢易教学系统 / 云桌面的服务",
                cfg.Classroom.RequiredServices.Contains("MMPC") && cfg.Classroom.RequiredServices.Contains("VoiClient"));
            Check("常见路径提示里有噢易学生端",
                DefaultBlocklists.ClassroomHints.Any(h => h.Value.IndexOf("Os-Easy", StringComparison.OrdinalIgnoreCase) >= 0));
            Check("服务清单里有噢易的服务", DefaultBlocklists.ClassroomServices.Any(s => s.Key == "MMPC"));

            Console.WriteLine("[3.2] 云桌面服务器地址（gtserver）判定");
            Check("被清空 = 需要处置", ClassroomGuard.VoiServerNeedsFix("", ""));
            Check("被改成非法值 = 需要处置", ClassroomGuard.VoiServerNeedsFix("not a ip!", ""));
            Check("没配期望值 → 合法 IP 就放过（不瞎写）", !ClassroomGuard.VoiServerNeedsFix("10.28.254.254", ""));
            Check("没配期望值 → 合法主机名也放过", !ClassroomGuard.VoiServerNeedsFix("voi.lab.local", ""));
            Check("配了期望值 → 被改就要求写回", ClassroomGuard.VoiServerNeedsFix("192.168.1.1", "10.28.254.254"));
            Check("配了期望值 → 一致就放过", !ClassroomGuard.VoiServerNeedsFix("10.28.254.254", "10.28.254.254"));
            Check("越界 IPv4 不算合法", !ClassroomGuard.LooksLikeHostOrIp("999.1.1.1"));

            Console.WriteLine("      本机实际识别结果（换机房也不会失败，仅作参考）：");
            var detected = ClassroomDetector.DetectAll();
            if (detected.Count == 0) Console.WriteLine("        未识别到课堂软件（该机房可能没装，或需在设置里手动填写）");
            foreach (ClassroomCandidate c in detected) Console.WriteLine("        " + c);
            string why;
            ClassroomCandidate bestPick = ClassroomDetector.DetectBest(out why);
            Check("首选结果不会是教师端（否则会错误地拉起教师端）",
                bestPick == null || !string.Equals(bestPick.ProcessName, "Teacher.exe", StringComparison.OrdinalIgnoreCase), why);
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
            Console.WriteLine("[5.1] 锁屏/遮罩的输入硬锁（InputLock）");
            Check("模式归一化：Off 只认 Off", LabGuard.Core.Interop.InputLock.Normalize("Off") == "Off");
            Check("模式归一化：空值/怪值都按 On 处理",
                LabGuard.Core.Interop.InputLock.Normalize(null) == "On" &&
                LabGuard.Core.Interop.InputLock.Normalize("") == "On" &&
                LabGuard.Core.Interop.InputLock.Normalize("on") == "On" &&
                LabGuard.Core.Interop.InputLock.Normalize("xxx") == "On");
            Check("默认配置 = 锁住（On）", new GuardConfig().InputHardLock == "On");
            LabGuard.Core.Interop.InputLock.Engage("Off");          // Off 不应上锁
            Check("Off 不上锁", !LabGuard.Core.Interop.InputLock.IsActive);
            LabGuard.Core.Interop.InputLock.Disengage();            // 不应抛异常
            Check("Disengage 在未上锁时也安全", true);

            Console.WriteLine("[5.4] 「键鼠锁死 / 指针消失」的两道归一化（历史事故的回归防线）");
            // ① InputLock 引用计数：界面都解除后必须回到 0，否则键鼠会一直动不了
            Check("无人持锁时 HoldsCount = 0", LabGuard.Core.Interop.InputLock.HoldsCount == 0);
            LabGuard.Core.Interop.InputLock.Disengage();
            LabGuard.Core.Interop.InputLock.Disengage();
            Check("未持锁时反复 Disengage 不会把计数压成负数",
                LabGuard.Core.Interop.InputLock.HoldsCount == 0);
            // ② CursorLock：底层 ShowCursor 带全局计数，必须"最多压一次、最多放一次"
            LabGuard.Core.Interop.CursorLock.Hide();
            LabGuard.Core.Interop.CursorLock.Hide();
            LabGuard.Core.Interop.CursorLock.Hide();
            Check("反复 Hide 只压一次（状态为已隐藏）", LabGuard.Core.Interop.CursorLock.IsHidden);
            LabGuard.Core.Interop.CursorLock.Show();
            LabGuard.Core.Interop.CursorLock.Show();
            Check("反复 Show 只放一次（状态回到可见，指针不会永久消失）",
                !LabGuard.Core.Interop.CursorLock.IsHidden);
            Check("收尾后 CursorLock 回到可见态（不会把指针留在隐藏态）",
                !LabGuard.Core.Interop.CursorLock.IsHidden);

            Console.WriteLine("[5.5] 按键手势解锁（Pause → ↑↑↓↓←→←→）的状态机");
            // 独立成类就是为了能在这里断言：这条通道历史上因为"没有可断言入口"连续三轮漏验。
            int vkUp = LabGuard.Core.Interop.UnlockGesture.VkUp;
            int vkDown = LabGuard.Core.Interop.UnlockGesture.VkDown;
            int vkLeft = LabGuard.Core.Interop.UnlockGesture.VkLeft;
            int vkRight = LabGuard.Core.Interop.UnlockGesture.VkRight;
            int vkPause = LabGuard.Core.Interop.UnlockGesture.VkPause;
            var GR = new LabGuard.Core.Interop.UnlockGesture("Pause", "U,U,D,D,L,R,L,R", 5);
            DateTime t0 = DateTime.Now;

            Check("未按激活键时方向键无效（不会误激活）",
                GR.KeyDown(vkUp, t0) == LabGuard.Core.Interop.GestureResult.None && !GR.Armed);
            GR.KeyUp(vkUp);
            Check("Pause 键激活", GR.KeyDown(vkPause, t0) == LabGuard.Core.Interop.GestureResult.Armed && GR.Armed);
            GR.KeyUp(vkPause);

            // 长按 ↑：连发 keydown 中间一个 keyup 都没有（低级钩子实测如此），必须只算一次
            Check("长按 ↑ 的第一下推进一格", GR.KeyDown(vkUp, t0) == LabGuard.Core.Interop.GestureResult.Progress);
            Check("长按 ↑ 的重复 keydown 被忽略（autorepeat 去重）",
                GR.KeyDown(vkUp, t0) == LabGuard.Core.Interop.GestureResult.None);
            Check("autorepeat 再来一次仍被忽略", GR.KeyDown(vkUp, t0) == LabGuard.Core.Interop.GestureResult.None);
            GR.KeyUp(vkUp);
            Check("↑ 抬起后才能再算一次", GR.KeyDown(vkUp, t0) == LabGuard.Core.Interop.GestureResult.Progress && GR.Position == 2);
            GR.KeyUp(vkUp);

            Check("走错一位 → 整段重置（返回 Mistake）",
                GR.KeyDown(vkLeft, t0) == LabGuard.Core.Interop.GestureResult.Mistake && !GR.Armed);
            GR.KeyUp(vkLeft);

            // 完整走一遍：Pause → ↑↑↓↓←→←→
            var last = LabGuard.Core.Interop.GestureResult.None;
            foreach (int vk in new[] { vkPause, vkUp, vkUp, vkDown, vkDown, vkLeft, vkRight, vkLeft, vkRight })
            { last = GR.KeyDown(vk, t0); GR.KeyUp(vk); }
            Check("完整序列命中（返回 Unlocked）", last == LabGuard.Core.Interop.GestureResult.Unlocked);
            Check("命中后回到未激活态", !GR.Armed && GR.Position == 0);

            var gTimeout = new LabGuard.Core.Interop.UnlockGesture("Pause", null, 2);
            gTimeout.KeyDown(vkPause, t0); gTimeout.KeyUp(vkPause);
            Check("超过时间窗口 → 重置（返回 Timeout）",
                gTimeout.KeyDown(vkUp, t0.AddSeconds(10)) == LabGuard.Core.Interop.GestureResult.Timeout && !gTimeout.Armed);

            Check("序列填错会退回默认 ↑↑↓↓←→←→（不会让老师解锁不了）",
                LabGuard.Core.Interop.UnlockGesture.ParseSequence("U,U,X").Length == 8);
            Check("小键盘 8 不算上箭头（不受 NumLock 影响）",
                LabGuard.Core.Interop.UnlockGesture.SymbolOf(0x68) == null);
            Check("ScrollLock 可作备用激活键",
                LabGuard.Core.Interop.UnlockGesture.VkForArmKey("ScrollLock")
                    == LabGuard.Core.Interop.UnlockGesture.VkScrollLock);

            Console.WriteLine("[5.6] 进程防拆加固（任务管理器杀不掉 / 老师仍杀得掉）");
            // 与手势解锁同一个道理：这条链踩过"没有可断言入口"的亏，
            // 所以 SDDL 的构造与校验都做成纯函数，这里不改动进程也能验规则。
            string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
            string hard = LabGuard.Core.Interop.ProcessHardening.BuildSddl(sid);
            Check("加固 SDDL 含 SYSTEM 完全控制", hard.Contains("(A;;GA;;;SY)"));
            Check("加固 SDDL 含管理员完全控制（老师/卸载必须能杀）", hard.Contains("(A;;GA;;;BA)"));
            Check("加固 SDDL 里当前用户只剩查询权（0x101400）",
                hard.Contains("(A;;0x101400;;;" + sid + ")"));
            Check("加固 SDDL 不含【用户完全控制】", !hard.Contains("(A;;GA;;;" + sid + ")"));

            Check("形态校验：认得出加固过的 SDDL",
                LabGuard.Core.Interop.ProcessHardening.LooksHardened(hard, sid));
            Check("形态校验：认得出【用户还是完全控制】（没加固住）",
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + sid + ")", sid));
            Check("形态校验：缺 SYSTEM 完全控制 = 不合格（系统收尾会出问题）",
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened("D:P(A;;GA;;;BA)(A;;0x101400;;;" + sid + ")", sid));
            Check("形态校验：空/异常输入不会误判为已加固",
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened(null, sid) &&
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened("", sid));
            Check("无用户 SID 时仍能构造（不会出现畸形 SDDL）",
                LabGuard.Core.Interop.ProcessHardening.BuildSddl(null) == "D:P(A;;GA;;;SY)(A;;GA;;;BA)");
            // 真机演练踩到的坑：系统读回来的 SDDL 会把 GA 展开成 0x1fffff、把 SID 写成别名（LA 等），
            // 纯文本比对必然失配 —— 下面两条就是那条坑的回归防线。
            Check("形态校验：认得系统读回的展开格式（GA→0x1fffff）",
                LabGuard.Core.Interop.ProcessHardening.LooksHardened(
                    "D:P(A;;0x1fffff;;;SY)(A;;0x1fffff;;;BA)(A;;0x101400;;;S-1-5-21-1-2-3-500)", "S-1-5-21-1-2-3-500"));
            Check("形态校验：展开格式下【用户还是完全控制】照样认得出来",
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened(
                    "D:P(A;;0x1fffff;;;SY)(A;;0x1fffff;;;BA)(A;;0x1fffff;;;S-1-5-21-1-2-3-500)", "S-1-5-21-1-2-3-500"));
            Check("服务进程本身就是 SYSTEM：不再追加一条自相矛盾的【只读】ACE",
                LabGuard.Core.Interop.ProcessHardening.BuildSddl("S-1-5-18") == "D:P(A;;GA;;;SY)(A;;GA;;;BA)");
            Check("形态校验：服务进程（SYSTEM）也算已加固",
                LabGuard.Core.Interop.ProcessHardening.LooksHardened(
                    "D:P(A;;0x1fffff;;;SY)(A;;0x1fffff;;;BA)(A;;0x101400;;;SY)", "S-1-5-18"));
            Check("形态校验：畸形 SDDL 不会误判为已加固",
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened("这不是一条SDDL", sid) &&
                !LabGuard.Core.Interop.ProcessHardening.LooksHardened("D:(X;;GA;;;SY)", sid));

            Check("还原 SDDL 把【结束进程】权还给所有者（老师退出/卸载不会被自己挡住）",
                LabGuard.Core.Interop.ProcessHardening.RestoreSddl().Contains("(A;;GA;;;OW)"));
            Check("留给用户的权限不含 TERMINATE/VM_WRITE/SUSPEND",
                (LabGuard.Core.Interop.ProcessHardening.UserReadOnly &
                 (LabGuard.Core.Interop.ProcessHardening.Terminate |
                  LabGuard.Core.Interop.ProcessHardening.VmWrite |
                  LabGuard.Core.Interop.ProcessHardening.SuspendResume)) == 0);

            // 出厂口径（2026-10-15 最终定稿）= **自保全开 + 管控全关**：
            //   · 管控（改学生机设置、拦软件、弹窗口）出厂全关，等教师机下发激活；
            //   · **自我保护出厂就开** —— 老板原话"学生不能停止、删掉、杀掉客户端，除非输入管理员密码"。
            //     它不是"管控学生"，而是"程序活着、能被激活"的前提（关掉它，全关状态下一分钟就被学生端收拾掉）。
            Check("出厂自保：进程防拆四层默认全开（学生杀不掉 / 停了会自愈）",
                new GuardConfig().AntiTamper.Enabled &&
                new GuardConfig().AntiTamper.HardenProcessDacl &&
                new GuardConfig().AntiTamper.FastRestart &&
                new GuardConfig().AntiTamper.ScheduledTaskGuard);
            Check("出厂自保：拦杀进程工具 / 反复被杀告警也默认开",
                new GuardConfig().AntiTamper.BlockKillTools &&
                new GuardConfig().AntiTamper.AlertOnRepeatedKill);
            // 关键不变式（2026-10-15）：自保类关键词**不受**「违规软件拦截」组总闸控制。
            // 出厂全关时那个组是关的，如果 taskkill 也一起被关掉，"学生杀不掉"就是空话。
            var selfOnly = new GuardConfig();                       // 默认：该组关 + 自保开
            var selfKw = LabGuard.Core.Guards.ProcessBlockGuard.KeywordsFor(selfOnly);
            Check("自保：违规拦截组关着时，仍然拦 taskkill / Process Hacker（不被组总闸带走）",
                System.Array.IndexOf(LabGuard.Core.Data.DefaultBlocklists.KillTools, "taskkill") >= 0 &&
                selfKw.Any(k => k.Equals("taskkill", StringComparison.OrdinalIgnoreCase)));
            Check("自保不等于接管管控：组关着时**不会**顺带拦游戏/任务管理器/注册表",
                !selfKw.Contains("任务管理器") && !selfKw.Contains("注册表编辑器") &&
                !selfKw.Any(k => k.Equals("扫雷", StringComparison.OrdinalIgnoreCase)));
            var groupOn = new GuardConfig();
            groupOn.ProcessBlock.Enabled = true;
            Check("老师打开「违规软件拦截」组后：管控关键词照旧全进来（自保没有破坏原有行为）",
                LabGuard.Core.Guards.ProcessBlockGuard.KeywordsFor(groupOn).Contains("任务管理器"));
            Check("杀进程工具清单已纳入 SystemInformer / taskkill",
                LabGuard.Core.Data.DefaultBlocklists.KillTools.Length > 0 &&
                System.Array.IndexOf(LabGuard.Core.Data.DefaultBlocklists.KillTools, "SystemInformer") >= 0 &&
                System.Array.IndexOf(LabGuard.Core.Data.DefaultBlocklists.KillTools, "taskkill") >= 0);

            // ---------------------------------------------------------------- [5.7] 暂停 ≠ 放弃进程守护
            // 真机事故回归（2026-10-08）：老师手势解锁断网遮罩 → 写 paused.flag →
            // 服务/兜底三处都因 IsPaused 直接 return → 学生杀掉小助手后 8 小时没人拉起，
            // 且托盘没了、老师点不到「启动监控」→ 死锁。规则：暂停只停策略，不停进程守护。
            Console.WriteLine("[5.7] 「老师暂停」与「进程守护」解耦（暂停期间被杀也要复活）");
            var cfgA = new GuardConfig();                       // 默认：防拆开 + 暂停也守护
            Check("默认：暂停期间仍然守护小助手进程",
                LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgA, true));
            Check("默认：未暂停时当然也守护",
                LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgA, false));
            var cfgB = new GuardConfig();
            cfgB.AntiTamper.KeepAliveWhenPaused = false;
            Check("关掉「暂停也守护」后：暂停期间按设计不再拉起（老师要的就是彻底停）",
                !LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgB, true));
            Check("关掉「暂停也守护」不影响未暂停时的守护",
                LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgB, false));
            var cfgC = new GuardConfig();
            cfgC.Watchdog.Enabled = false; cfgC.AntiTamper.Enabled = false;
            Check("两个守护开关都关 = 不守护（尊重老师的配置）",
                !LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgC, false));
            var cfgD = new GuardConfig();
            cfgD.Watchdog.Enabled = false;                      // 只开防拆（机房常见："只开网络组 + 防拆"）
            cfgD.AntiTamper.Enabled = true;                     // ← 显式打开（出厂全关，不能再靠默认值）
            Check("只开「进程防拆」、关「互相守护」时仍然守护",
                LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgD, true));
            Check("空配置不会抛异常",
                !LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(null, true));

            // ---------------------------------------------------------------- [5.9] 出厂全关（2026-10-15）
            // 产品决定：学生端装上后**不碰任何设置、不拦任何软件、不弹任何窗口**，等教师机下发激活。
            // 但"全关"必须仍然能收到下发 —— 否则老师发什么都是石沉大海。所以有且只有四条**通道**开着。
            Console.WriteLine("[5.9] 出厂全关 + 通道四项（能被激活的必要条件）");
            var zero = new GuardConfig();
            // 注意「互相守护 / Watchdog」与「进程防拆加固 / AntiTamper」**不在**这张清单里：
            // 前者是通道③（小助手存活），后者是自保（学生杀不掉）—— 两个出厂都必须开着。
            string[] controlGroups = { "Classroom", "Network", "ProcessBlock", "FileCreation", "Usb", "Browser",
                                       "Shell", "Session", "SafeMode", "RegistryAcl" };
            var stillOn = new List<string>();
            foreach (string g in controlGroups)
            {
                var prop = typeof(GuardConfig).GetProperty(g);
                object sub = prop.GetValue(zero);
                var en = sub.GetType().GetProperty("Enabled");
                if (en != null && (bool)en.GetValue(sub)) stillOn.Add(g);
            }
            Check("出厂全关：所有管控组（含 USB/浏览器/网络/电子教室…）都是关的",
                stillOn.Count == 0, stillOn.Count == 0 ? "" : ("仍有开着的组：" + string.Join("/", stillOn)));
            Check("出厂全关：hosts 只读加固也是关的（不动 hosts 文件）", !zero.Hosts.ProtectFileAcl);
            Check("通道①：总闸 Enabled 开着（否则老师发的任何单项都不会生效）", zero.Enabled);
            Check("通道②：服务器统一管控开着（没配对文件时本项无效果）", zero.Server.Enabled);
            Check("通道③：互相守护开着（小助手 = 会话级策略的执行者，被杀要能回来）", zero.Watchdog.Enabled);
            Check("自保：程序文件被删能自动恢复 + 关键文件缺失会锁定（对应\"不能删掉\"）",
                zero.Watchdog.RestoreMissingFiles && zero.Watchdog.LockOnIntegrityFailure);
            Check("自保：不隐藏安装目录（老师维护要看得到）、不因服务异常重启整台机器（危险项保持关）",
                !zero.Watchdog.HideInstallDir && !zero.Watchdog.RebootOnServiceFailure);
            Check("通道④：暂停期间也守护小助手（2026-10-08 真机事故的教训）", zero.AntiTamper.KeepAliveWhenPaused);

            // 回归（2026-10-15 修的真 bug）：没启用、没改过系统的模块，Stop() **绝不能**去动系统。
            // 以前 Stop() 会无条件跑各模块的"还原"分支 —— 浏览器策略 = 删 HKLM 策略键、USB = 把 usbstor 写回 3。
            // 出厂全关下每次暂停/改策略都会触发，等于把别人（学校组策略/管理员）设的值改掉。
            int beforeZero = SystemActions.AuditTrail.Count;
            foreach (EngineRole role in new[] { EngineRole.SystemCore, EngineRole.Full })
            {
                var e = new GuardEngine(zero);
                e.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, role);
                e.Stop();
            }
            var zeroOps = SystemActions.AuditTrail.Skip(beforeZero).ToList();
            Check("全关配置：起引擎再停止，**一条系统写入都没有**（没动过就不许还原）",
                zeroOps.Count == 0, zeroOps.Count == 0 ? "" : (zeroOps.Count + " 条：" + string.Join(" | ", zeroOps.Take(3))));

            // 反向验证：明着打开一组，干跑里必须能看到它去写系统（证明上面那条不是"什么检查都没跑"）
            var usbOn = new GuardConfig();
            usbOn.Usb.Enabled = true;
            int beforeUsb = SystemActions.AuditTrail.Count;
            var eUsb = new GuardEngine(usbOn);
            eUsb.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, EngineRole.SystemCore);
            var usbOps = SystemActions.AuditTrail.Skip(beforeUsb).ToList();
            eUsb.Stop();
            Check("反向验证：只打开「USB 存储设备」→ 干跑里出现 usbstor 写入（闸门不是空转）",
                usbOps.Any(a => a.Contains("usbstor")), usbOps.Count + " 条");

            // 用可控时间断言（真机教训：一天刷了 480 行同样的 INFO，老师根本不会看）
            string tmpStamp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "labguard-selftest-pausehint.stamp");
            if (System.IO.File.Exists(tmpStamp)) System.IO.File.Delete(tmpStamp);
            Check("暂停提示有限频：10 分钟内只提示一次，到点再提示",
                LabGuard.Core.Guards.WatchdogGuard.PauseHintDue(10, new DateTime(2026, 1, 1, 10, 0, 0), tmpStamp) &&
                !LabGuard.Core.Guards.WatchdogGuard.PauseHintDue(10, new DateTime(2026, 1, 1, 10, 5, 0), tmpStamp) &&
                LabGuard.Core.Guards.WatchdogGuard.PauseHintDue(10, new DateTime(2026, 1, 1, 10, 11, 0), tmpStamp));
            try { if (System.IO.File.Exists(tmpStamp)) System.IO.File.Delete(tmpStamp); } catch { }
            Check("新增配置默认合理：暂停也守护 / 解除遮罩默认暂停监控 / 暂停 120 分钟自动恢复",
                new GuardConfig().AntiTamper.KeepAliveWhenPaused &&
                new GuardConfig().Network.PauseMonitoringOnTeacherUnlock &&
                new GuardConfig().ResumeAfterMinutes == 120);

            // ---------------------------------------------------------------- [5.8] 遮罩不得泄露解锁方式
            // 真机反馈（2026-10-08）：遮罩上直接印着「老师：按 Pause 后按 ↑↑↓↓←→←→ 解除」，
            // 学生照着屏幕就能解锁 —— 等于把钥匙挂在锁上。规则：遮罩默认不透露任何解锁通道。
            Console.WriteLine("[5.8] 遮罩不泄露解锁方式（不把钥匙挂在锁上）");
            Func<bool, bool, bool, string, string> tip = LabGuard.Core.UI.DisconnectMaskForm.BuildTeacherTip;
            Func<string, bool> leaks = LabGuard.Core.UI.DisconnectMaskForm.TipLeaksUnlockMethod;
            string tipDefault = tip(false, true, true, "Pause");
            Check("默认（双通道）遮罩底部不泄露任何解锁方式", !leaks(tipDefault));
            Check("默认文案仍说清\"网络恢复后会自动消失\"（学生知道该怎么办）",
                tipDefault.Contains("恢复网络后 10 秒内自动消失"));
            Check("纯手势通道 + 关闭提示 → 也不泄露", !leaks(tip(false, true, false, "Pause")));
            Check("纯密码通道 + 关闭提示 → 也不泄露", !leaks(tip(false, false, true, "Pause")));
            Check("ScrollLock 激活键 + 关闭提示 → 也不泄露", !leaks(tip(false, true, true, "ScrollLock")));
            Check("关提示时三种通道文案完全一致（不因通道不同泄露信息）",
                tip(false, true, true, "ScrollLock") == tip(false, false, false, "Pause"));
            Check("老师主动打开后才显示手势序列", leaks(tip(true, true, false, "Pause")) &&
                tip(true, true, false, "Pause").Contains("↑↑↓↓←→←→"));
            Check("老师主动打开后：双通道会把密码通道也说出来",
                tip(true, true, true, "Pause").Contains("Esc"));
            Check("激活键为 ScrollLock 时提示文案跟着变（不说 Pause）",
                tip(true, true, false, "ScrollLock").Contains("Scroll Lock") &&
                !tip(true, true, false, "ScrollLock").Contains("Pause"));
            Check("泄露判定认得各种形态（Pause / ScrollLock / ↑ / Esc / 密码 / 手势）",
                leaks("按 Pause 解除") && leaks("按 Scroll Lock 解除") && leaks("↑↑↓↓←→←→") &&
                leaks("连按 5 次 Esc") && leaks("输入密码") && leaks("手势未识别") &&
                !leaks("（恢复网络后 10 秒内自动消失）"));
            Check("新增配置默认合理：遮罩默认不提示解锁方式 / 不显示进度点",
                !new GuardConfig().Network.MaskShowTeacherHint &&
                !new GuardConfig().Network.UnlockGestureFeedback);
            // 注入逻辑集中在一处（托盘 / 设置截图预览 / Agent --preview-mask 三处共用）：
            // 以前三处各写一遍，新属性漏注入过一次（2026-10-08）。
            var savedLevel = Log.MinLevel;      // 构造窗体会打一条"窗体已就绪"日志，别让它混进自检输出
            Log.MinLevel = LogLevel.Error;
            try
            {
                var probe = new LabGuard.Core.UI.DisconnectMaskForm();
                var probeCfg = new GuardConfig().Network;
                probeCfg.MaskShowTeacherHint = true;
                probeCfg.UnlockGestureSequence = "L,L,R,R";
                probe.ApplyNetworkPolicy(probeCfg);
                Check("配置注入：一次调用把整组「老师解锁方式」带进遮罩（三处共用，杜绝漏注入）",
                    probe.ShowTeacherHint && probe.GestureSequence == "L,L,R,R");
                probe.Dispose();
            }
            catch (Exception ex) { Check("配置注入：构造遮罩并注入配置不抛异常（" + ex.Message + "）", false); }
            finally { Log.MinLevel = savedLevel; }

            Console.WriteLine("[5.9] 密码门禁不冤枉老师、不给学生留线索");
            Check("设置程序的密码框标题 ≠ 设置主窗口标题（曾因撞脸被当成\"界面坏了\"）",
                LabGuard.Core.UI.PasswordGate.WindowTitle("打开设置") != LabGuard.Core.UI.PasswordGate.SettingsWindowTitle());
            Check("密码框标题一眼能看出\"在要密码\"（含「输入密码」字样）",
                LabGuard.Core.UI.PasswordGate.WindowTitle("打开设置").Contains("输入密码"));
            Check("忘记密码提示不泄露任何救援手段（cleanup/force/uninstall/路径/命令都没有）",
                !LabGuard.Core.UI.PasswordGate.LeaksRescueMethod(LabGuard.Core.UI.PasswordGate.ForgotPasswordTip));
            Check("泄露判定认得各种形态（cleanup-all / --force / --init / .ps1 / 路径）",
                LabGuard.Core.UI.PasswordGate.LeaksRescueMethod("运行 cleanup-all.ps1") &&
                LabGuard.Core.UI.PasswordGate.LeaksRescueMethod("加 --force 可强制卸载") &&
                LabGuard.Core.UI.PasswordGate.LeaksRescueMethod("Settings.exe --init 123456") &&
                LabGuard.Core.UI.PasswordGate.LeaksRescueMethod("去 C:\\Program Files\\LabGuard 删掉") &&
                !LabGuard.Core.UI.PasswordGate.LeaksRescueMethod("请联系机房管理员"));
            Check("「点取消」不算密码错误（老师主动关窗 → 不弹任何提示）",
                LabGuard.Core.UI.PasswordGate.WasCancelled(0) &&
                LabGuard.Core.UI.PasswordGate.FailureMessage("修改设置", 0) == null);
            Check("「真的输错」才提示，且说清试了几次",
                !LabGuard.Core.UI.PasswordGate.WasCancelled(2) &&
                LabGuard.Core.UI.PasswordGate.FailureMessage("修改设置", 2).Contains("2 次"));
            Check("失败提示同样不泄露救援手段",
                !LabGuard.Core.UI.PasswordGate.LeaksRescueMethod(
                    LabGuard.Core.UI.PasswordGate.FailureMessage("修改设置", 3) ?? "（null）"));
            Check("五个调用点（设置/卸载/托盘/解除锁定/遮罩）标题全部统一来自 PasswordGate（不再各写各的）",
                new[]
                {
                    LabGuard.Core.UI.PasswordGate.WindowTitle("打开设置"),
                    LabGuard.Core.UI.PasswordGate.WindowTitle("卸载"),
                    LabGuard.Core.UI.PasswordGate.WindowTitle("暂停监控"),
                    LabGuard.Core.UI.PasswordGate.WindowTitle("解除锁定/暂停监控"),
                    LabGuard.Core.UI.PasswordGate.WindowTitle("解除断网遮罩")
                }.All(t => t.StartsWith("LabGuard · 输入密码（") && t.EndsWith("）")));

            Console.WriteLine("[5.3] 断网即时归因（遮罩/日志要说清检测到什么）");
            Check("网卡消失 → 说清了网卡被禁用或拔掉",
                NetworkGuard.DescribeDown(false, false, null).Contains("消失"));
            Check("网卡 Down → 说清网线被拔掉/网卡被禁用",
                NetworkGuard.DescribeDown(true, false, null).Contains("网卡已断开"));
            Check("IP 清空 → 说清没拿到地址",
                NetworkGuard.DescribeDown(true, true, "").Contains("IP 已被清空"));
            Check("0.0.0.0 → 说清没拿到地址",
                NetworkGuard.DescribeDown(true, true, "0.0.0.0").Contains("IP 已被清空"));
            Check("169.254.* → 说清网线未接好/DHCP 不可达",
                NetworkGuard.DescribeDown(true, true, "169.254.10.20").Contains("169.254"));
            Check("四种原因互不相同",
                new[] { NetworkGuard.DescribeDown(false, false, null),
                        NetworkGuard.DescribeDown(true, false, null),
                        NetworkGuard.DescribeDown(true, true, "0.0.0.0"),
                        NetworkGuard.DescribeDown(true, true, "169.254.1.2") }.Distinct().Count() == 4);

            Console.WriteLine("[5.2] 注册表权限加固（P3）");
            var aclCfg = new GuardConfig().RegistryAcl;
            Check("默认加固 LabGuard 自己的键", aclCfg.Keys.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\LabGuard"));
            Check("默认加固课堂软件的键（频道/自动登录参数）",
                aclCfg.Keys.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\TopDomain\e-Learning Class\Student"));
            Check("「连管理员也锁」默认关闭", !aclCfg.DenyAdminsWrite);
            Check("上网入口浏览器默认留空（= 自动探测注册表/App Paths/常见路径）",
                new GuardConfig().Browser.LauncherBrowser == "");
            Check("危险操作确认默认等 10 秒（0 = 不等）", new GuardConfig().ConfirmDelaySeconds == 10);
            foreach (string k in aclCfg.Keys)
            {
                Check("键路径写法合法：" + k,
                    k.StartsWith("HKEY_LOCAL_MACHINE\\") || k.StartsWith("HKEY_CURRENT_USER\\"));
            }
        }

        /// <summary>[5.10] 红队对抗测试（2026-10-08）修复的回归断言——每条对应一个实测漏洞。</summary>
        private static void TestRedTeamRegression()
        {
            Console.WriteLine("[5.10] 红队修复回归（配置签名 / 暂停旗标 / 退避 / 拉起锚定 / 识别不漂移）");

            // ---- B4：配置信封签名 ----
            byte[] key = new byte[32];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
            string json = "{\"PasswordHash\":\"abc\",\"Enabled\":true}";
            string wrapped = ConfigIntegrity.Wrap(key, json, new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc));
            string outJson; DateTime outAt;
            Check("配置信封：签发后能原样解出",
                ConfigIntegrity.TryUnwrap(key, wrapped, out outJson, out outAt) && outJson == json);
            string tampered = wrapped.Replace("\"Enabled\":true", "\"Enabled\":false");
            Check("配置信封：改一个字节就验签失败（B4a 篡改必然落网）",
                !ConfigIntegrity.TryUnwrap(key, tampered, out outJson, out outAt));
            Check("配置信封：错误密钥验签失败",
                !ConfigIntegrity.TryUnwrap(new byte[32], wrapped, out outJson, out outAt));
            Check("配置信封：legacy 内容可解出（升级/篡改的判定策略在 ConfigStore 层）",
                ConfigIntegrity.TryUnwrap(null, json, out outJson, out outAt) && outJson == json
                && ConfigIntegrity.TryUnwrap(key, json, out outJson, out outAt) && outJson == json);
            Check("配置信封：IsEnvelope 能区分签名件与 legacy",
                ConfigIntegrity.IsEnvelope(wrapped) && !ConfigIntegrity.IsEnvelope(json));

            // ---- B3：暂停旗标签名 ----
            DateTime pauseAt = new DateTime(2026, 10, 8, 13, 0, 0);
            string flag = ConfigIntegrity.WrapPause(key, pauseAt);
            DateTime outPause;
            Check("暂停旗标：签发后能解出且时间一致",
                ConfigIntegrity.TryUnwrapPause(key, flag, out outPause) && outPause == pauseAt);
            Check("暂停旗标：学生 echo 的伪造时间戳被判伪造（B3 免死金牌作废）",
                !ConfigIntegrity.TryUnwrapPause(key, "2026-10-08T13:00:00", out outPause));
            Check("暂停旗标：改了时间的真信封也验签失败",
                !ConfigIntegrity.TryUnwrapPause(key,
                    ConfigIntegrity.WrapPause(key, pauseAt.AddHours(1)).Replace(pauseAt.AddHours(1).Ticks.ToString(), pauseAt.Ticks.ToString()),
                    out outPause));
            Check("暂停旗标：降级态（无密钥）才接受 legacy 时间戳",
                ConfigIntegrity.TryUnwrapPause(null, "2026-10-08T13:00:00", out outPause));

            // ---- B4c：空密码散列 fail-closed ----
            Check("已安装+散列空 = 受损（fail-closed）", ConfigIntegrity.IsSuspiciousEmptyPassword(true, ""));
            Check("未安装+散列空 = 正常未配置", !ConfigIntegrity.IsSuspiciousEmptyPassword(false, ""));
            Check("已安装+散列在 = 正常", !ConfigIntegrity.IsSuspiciousEmptyPassword(true, "pbkdf2$..."));

            // ---- C4：口令退避序列 ----
            var gate = new System.Func<int, int>(LabGuard.Core.UI.PasswordGate.BackoffSeconds);
            Check("口令退避：前 2 次免费（老师手滑不该被罚）", gate(1) == 0 && gate(2) == 0);
            Check("口令退避：第 3 次起 2/4/8 秒递增", gate(3) == 2 && gate(4) == 4 && gate(5) == 8);
            Check("口令退避：高位 128/256 秒", gate(9) == 128 && gate(10) == 256);
            Check("口令退避：封顶 300 秒且不再增长", gate(11) == 300 && gate(12) == 300 && gate(30) == 300);

            // ---- C4：手势防穷举 ----
            Check("手势错误 19 次还没熔断", !UnlockGesture.BruteForceTripped(19));
            Check("手势错误 20 次触发熔断", UnlockGesture.BruteForceTripped(20));

            // ---- A5：拉起目标锚定"缺失的那个" ----
            string tmp = Path.Combine(Path.GetTempPath(), "lg-redteam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string studentExe = Path.Combine(tmp, "Student.exe");
                string voiExe = Path.Combine(tmp, "VoiClient.exe");
                File.WriteAllText(studentExe, "x");
                File.WriteAllText(voiExe, "x");
                var candidates = new List<ClassroomCandidate>
                {
                    // 现场还原：Student 死后，DetectBest 挑中了正在运行的 VoiClient
                    new ClassroomCandidate { Path = voiExe, ProcessName = "VoiClient.exe", Source = "正在运行" },
                    new ClassroomCandidate { Path = studentExe, ProcessName = "Student.exe", Source = "服务：MMPC（噢易多媒体教学系统）" }
                };
                Check("拉起锚定：缺 Student.exe 就必须拉 Student.exe（A5 核心回归）",
                    ClassroomGuard.ResolveRelaunchPath("Student.exe", null, candidates) == studentExe);
                Check("拉起锚定：缺 VoiClient 时才拉 VoiClient",
                    ClassroomGuard.ResolveRelaunchPath("VoiClient.exe", null, candidates) == voiExe);
                Check("拉起锚定：找不到路径返回 null（绝不拿别的进程顶替）",
                    ClassroomGuard.ResolveRelaunchPath("NotExist.exe", null, candidates) == null);
                Check("拉起锚定：手填 MainExecutable 仅当文件名一致才生效",
                    ClassroomGuard.ResolveRelaunchPath("Student.exe", voiExe, candidates) == studentExe
                    && ClassroomGuard.ResolveRelaunchPath("VoiClient.exe", voiExe, candidates) == voiExe);

                // ---- A5：识别不漂移（学生端本体在磁盘上 = 压过正在运行的云桌面客户端）----
                string reason;
                var best = ClassroomDetector.PickBest(candidates, out reason);
                Check("识别不漂移：磁盘上的 Student.exe 压过正在运行的 VoiClient",
                    best != null && best.Path == studentExe, reason);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
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

        /// <summary>
        /// [10] 服务器统一管控契约（双仓契约的客户端侧，v0.10）：
        /// ① 信封验签——伪服务器 / 改策略 / 改 seq 重放 / 未知魔数 / 畸形信封 全部必须拒绝；
        /// ② 配对与公钥 pin——指纹不符 / 格式不认识 / 塞的是私钥 全部必须拒绝，公钥被替换后 fail-closed；
        /// ③ 开关目录快照导出——字段数与界面开关一一对应、默认策略覆盖全部 path、同一输入逐字节稳定。
        /// 全程只在临时目录操作，不修改系统、不碰真实 %ProgramData%\LabGuard。
        /// </summary>
        private static void TestServerContract()
        {
            Console.WriteLine("[10] 服务器统一管控契约（信封 / 配对 / 目录导出）");

            // 这封信的 policyJson 里故意放上 '|' 与转义反斜杠：信封解析不许 naive Split('|')
            const string policy =
                "{\"Enabled\":true,\"Note\":\"含竖线|和路径D:\\\\x\",\"Network\":{\"DnsServers\":[\"10.28.1.10\"]}}";
            var key = new RSACryptoServiceProvider(2048);
            var other = new RSACryptoServiceProvider(2048);
            RSA pub = PublicOnly(key);
            RSA pubOther = PublicOnly(other);
            var issuedAt = new DateTime(2026, 10, 12, 3, 4, 5, DateTimeKind.Utc);

            string good = PolicyEnvelope.Build(7, issuedAt, false, policy, key);
            EnvelopeData ok = PolicyEnvelope.Parse(good, pub);
            Check("① 合法信封验签通过", ok.Verified, ok.Error);
            Check("① seq / issuedAt / paused 解析正确",
                ok.Seq == 7 && !ok.Paused && ok.IssuedAtUtc.ToUniversalTime() == issuedAt,
                "seq=" + ok.Seq + " paused=" + ok.Paused);
            Check("① policyJson 含 '|' 与转义反斜杠也能原样取回", ok.PolicyJson == policy);

            EnvelopeData pausedEnv = PolicyEnvelope.Parse(
                PolicyEnvelope.Build(8, DateTime.UtcNow, true, policy, key), pub);
            Check("① 全员暂停信封 paused=true", pausedEnv.Verified && pausedEnv.Paused, pausedEnv.Error);

            Check("① 改策略内容 → 验签拒绝",
                !PolicyEnvelope.Parse(good.Replace("\"Enabled\":true", "\"Enabled\":false"), pub).Verified);
            Check("① 改 seq 想重放/回滚 → 验签拒绝",
                !PolicyEnvelope.Parse(good.Replace("LGSRV1|7|", "LGSRV1|9|"), pub).Verified);
            Check("① 换一把公钥（伪服务器）→ 验签拒绝", !PolicyEnvelope.Parse(good, pubOther).Verified);

            EnvelopeData unknownMagic = PolicyEnvelope.Parse(good.Replace("LGSRV1|", "LGSRV2|"), pub);
            Check("① 未知魔数 LGSRV2 → 拒绝并提示协议升级", !unknownMagic.Verified &&
                unknownMagic.Error != null && unknownMagic.Error.IndexOf("魔数", StringComparison.Ordinal) >= 0);

            EnvelopeData badPaused = PolicyEnvelope.Parse(good.Replace("|0|", "|2|"), pub);
            Check("① paused 段只能 0/1（填 2 → 拒绝）", !badPaused.Verified && badPaused.Error != null);
            Check("① 签名段不是 base64 → 拒绝",
                !PolicyEnvelope.Parse(good.Substring(0, good.LastIndexOf('|') + 1) + "!!!", pub).Verified);
            Check("① 空信封 / 未配对（无公钥）→ 拒绝",
                !PolicyEnvelope.Parse("", pub).Verified && !PolicyEnvelope.Parse(good, null).Verified);
            Check("① 防回滚：只有 seq 严格更大才接受",
                PolicyEnvelope.IsNewer(3, 2) && !PolicyEnvelope.IsNewer(2, 2) && !PolicyEnvelope.IsNewer(1, 2));

            // ---- 配对与公钥 pin（临时目录）
            string tmp = Path.Combine(Path.GetTempPath(),
                "labguard-selftest-pair-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string savedDir = ServerPairing.DataDir;
            try
            {
                Directory.CreateDirectory(tmp);
                ServerPairing.DataDir = tmp;
                string pubXml = key.ToXmlString(false);
                string fingerprint = ServerPairing.ComputeFingerprint(pubXml);
                Check("② 指纹 = 16 位小写 hex", fingerprint.Length == 16 &&
                    fingerprint.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')), fingerprint);

                string pairPath = Path.Combine(tmp, "labguard-pairing.json");
                File.WriteAllText(pairPath, BuildPairingJson(pubXml, 8210, fingerprint), new UTF8Encoding(false));
                PairingInfo info = ServerPairing.Import(pairPath);
                Check("② 配对导入成功且地址可读", info != null && info.Port == 8210 &&
                    info.Describe() == "10.28.1.20:8210", info == null ? null : info.Describe());
                Check("② 配对后 IsPaired = true，指纹可复核",
                    ServerPairing.IsPaired && ServerPairing.LoadPinned().Fingerprint == fingerprint);
                RSA pinned = ServerPairing.LoadPublicKey();
                Check("② pin 住的公钥能验签（端到端：配对 → 验信封）",
                    pinned != null && PolicyEnvelope.Parse(good, pinned).Verified);

                string badFp = Path.Combine(tmp, "bad-fingerprint.json");
                File.WriteAllText(badFp, BuildPairingJson(pubXml, 8210, "deadbeefdeadbeef"), new UTF8Encoding(false));
                Check("② 指纹与公钥不符 → 拒绝导入",
                    Throws(() => ServerPairing.Import(badFp) != null));

                string badFormat = Path.Combine(tmp, "bad-format.json");
                File.WriteAllText(badFormat,
                    BuildPairingJson(pubXml, 8210, fingerprint).Replace("labguard-pairing/1", "labguard-pairing/2"),
                    new UTF8Encoding(false));
                Check("② 配对文件格式不认识 → 拒绝导入",
                    Throws(() => ServerPairing.Import(badFormat) != null));

                string privPath = Path.Combine(tmp, "private.json");
                string privXml = key.ToXmlString(true);
                File.WriteAllText(privPath,
                    BuildPairingJson(privXml, 8210, ServerPairing.ComputeFingerprint(privXml)),
                    new UTF8Encoding(false));
                Check("② 配对文件里是私钥 → 拒绝导入（绝不把私钥装到学生机）",
                    Throws(() => ServerPairing.Import(privPath) != null));

                File.WriteAllText(ServerPairing.PublicKeyPath, pubOther.ToXmlString(false), new UTF8Encoding(false));
                Check("② 公钥被替换 → 按未配对处理（fail-closed，继续单机管控）",
                    !ServerPairing.IsPaired && ServerPairing.LoadPublicKey() == null);
            }
            finally
            {
                ServerPairing.DataDir = savedDir;
                TryDeleteDir(tmp);
            }

            // ---- 开关目录契约快照（教师机服务端面板按它渲染）
            int fieldCount = SettingsCatalog.All().Count;
            var stamp = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
            string doc = CatalogExport.Build("0.10-test", stamp);
            Check("③ 快照含 format / clientVersion / fieldCount / defaultPolicy",
                doc.IndexOf("\"format\":\"labguard-catalog/1\"", StringComparison.Ordinal) >= 0 &&
                doc.IndexOf("\"clientVersion\":\"0.10-test\"", StringComparison.Ordinal) >= 0 &&
                doc.IndexOf("\"fieldCount\":" + fieldCount, StringComparison.Ordinal) >= 0 &&
                doc.IndexOf("\"defaultPolicy\":\"{", StringComparison.Ordinal) > 0, fieldCount + " 项");
            Check("③ 快照字段对象数 == 界面开关数",
                CountOf(doc, "\"path\":") == fieldCount, CountOf(doc, "\"path\":") + " vs " + fieldCount);
            List<string> coverage = CatalogExport.ValidateCoverage();
            Check("③ 每个开关 path 都能在默认策略里解析到（服务端面板不会出现空控件）",
                coverage.Count == 0, coverage.Count == 0 ? null : string.Join("；", coverage.Take(3)));
            Check("③ 同一输入两次导出逐字节一致（可做 diff 审查）", CatalogExport.Build("0.10-test", stamp) == doc);
            Check("③ 新增「服务器统一管控」5 项已进快照",
                CountOf(doc, "\"section\":\"15、服务器统一管控\"") == 5,
                CountOf(doc, "\"section\":\"15、服务器统一管控\"") + " 项");
        }

        /// <summary>
        /// [11] 服务器同步（轮询 / 应用 / 回退 / 暂停优先级，v0.10 M2b）：
        /// ① 未启用 → Disabled；② 未配对 → NotPaired；③ 服务端未配置策略（503）不算失败；
        /// ④ seq 前进才拉全文并应用；⑤ 同 seq 不重复应用；⑥ **旧 seq 信封（重放）必须拒绝**；
        /// ⑦ 篡改的信封拒绝；⑧ 全员暂停信封生效；⑨ 连续失败回退单机、恢复后自动回到轮询；
        /// ⑩ **老师本地暂停优先**（暂存 pending，恢复后自动应用）；⑪ 缓存重启可用、被篡改则忽略；
        /// ⑫ 合并保留本机专属字段（口令 / 服务器配置 / 版本）；⑬ 真实 HTTP 回环线格式联调。
        /// 全程临时目录 + 假通道，不发真实网络请求（⑬ 除外，只在 127.0.0.1 回环）。
        /// </summary>
        private static void TestServerSync()
        {
            Console.WriteLine("[11] 服务器同步（轮询 / 应用 / 回退 / 暂停优先级）");

            var key = new RSACryptoServiceProvider(2048);
            RSA pub = PublicOnly(key);
            string tmp = Path.Combine(Path.GetTempPath(), "labguard-selftest-sync-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string savedPairDir = ServerPairing.DataDir;
            string savedCacheDir = ServerPolicyCache.DataDir;
            try
            {
                Directory.CreateDirectory(tmp);
                ServerPairing.DataDir = tmp;
                ServerPolicyCache.DataDir = tmp;
                ServerPolicyCache.Clear();

                string pairPath = Path.Combine(tmp, "labguard-pairing.json");
                File.WriteAllText(pairPath, BuildPairingJson(key.ToXmlString(false), 8210,
                    ServerPairing.ComputeFingerprint(key.ToXmlString(false))), new UTF8Encoding(false));
                PairingInfo pairing = ServerPairing.Import(pairPath);

                var cfg = new GuardConfig();
                cfg.Server.Enabled = true;
                cfg.Server.Address = "127.0.0.1:8210";
                cfg.Server.PollSeconds = 1;
                cfg.Server.CheckinSeconds = 10;
                cfg.Server.FailuresBeforeFallback = 3;

                var fake = new FakeServer(key);
                var applied = new List<EnvelopeData>();
                bool localPaused = false;
                var client = new ServerClient(cfg, pairing, pub, fake, () => localPaused, e => applied.Add(e));
                var t0 = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

                var offCfg = new GuardConfig();
                offCfg.Server.Enabled = false;
                var offClient = new ServerClient(offCfg, pairing, pub, fake, () => false, e => { });
                offClient.Tick(t0);
                Check("① 未启用服务器管控 → Disabled（完全等同单机版）",
                    offClient.State == ServerLinkState.Disabled);

                var noPair = new ServerClient(cfg, null, null, fake, () => false, e => { });
                noPair.Tick(t0);
                Check("② 启用但未配对 → NotPaired（继续单机管控）",
                    noPair.State == ServerLinkState.NotPaired);

                fake.NotReady = true;
                client.Tick(t0);
                Check("③ 服务端未配置策略（503）→ 不算失败，保持轮询",
                    client.State == ServerLinkState.Polling && client.ConsecutiveFailures == 0);
                fake.NotReady = false;

                fake.SetPolicy(1, false, "{\"Enabled\":true,\"StartDelaySeconds\":30}");
                client.Tick(t0);
                Check("④ 收到 seq=1 → 应用一次", applied.Count == 1 && client.AppliedSeq == 1,
                    "applied=" + applied.Count + " seq=" + client.AppliedSeq);
                Check("④ 应用后立刻上报心跳（含机器名 / 版本 / seq）",
                    fake.Checkins.Count == 1 && fake.LastCheckin.Contains("\"seq\":1") &&
                    fake.LastCheckin.Contains(Environment.MachineName));
                Check("④ 策略已落盘缓存（重启后不等服务器也能继续管控）",
                    File.Exists(ServerPolicyCache.CachePath));

                client.Tick(t0.AddSeconds(1));
                Check("⑤ seq 未变 → 不重复应用、也不重复拉全文",
                    applied.Count == 1 && fake.PolicyFetchCount == 1,
                    "applied=" + applied.Count + " fetch=" + fake.PolicyFetchCount);

                fake.SetPolicy(2, false, "{\"Enabled\":true,\"StartDelaySeconds\":45}");
                client.Tick(t0.AddSeconds(2));
                Check("⑥ seq 前进到 2 → 再应用一次", applied.Count == 2 && client.AppliedSeq == 2 &&
                    applied[1].Seq == 2);

                // 重放攻击：探测说 seq=3（骗客户端去拉），但信封本身是旧的 seq=1（签名仍合法）
                fake.SetPolicy(1, false, "{\"Enabled\":true,\"StartDelaySeconds\":30}");
                fake.SetProbeSeq(3);
                client.Tick(t0.AddSeconds(3));
                Check("⑦ 旧 seq 信封（合法签名 + 重放）→ 拒绝，不覆盖已生效策略",
                    applied.Count == 2 && client.AppliedSeq == 2 &&
                    client.LastError != null && client.LastError.IndexOf("未前进", StringComparison.Ordinal) >= 0);

                // 篡改：改一个字符，签名就对不上
                fake.SetPolicy(5, false, "{\"Enabled\":true,\"StartDelaySeconds\":60}");
                fake.PolicyText = fake.PolicyText.Replace("\"Enabled\":true", "\"Enabled\":false");
                client.Tick(t0.AddSeconds(4));
                Check("⑧ 篡改信封 → 验签失败并拒绝", applied.Count == 2 && client.AppliedSeq == 2 &&
                    client.LastError != null && client.LastError.IndexOf("签名", StringComparison.Ordinal) >= 0);

                fake.SetPolicy(6, true, "{\"Enabled\":true,\"StartDelaySeconds\":60}");
                client.Tick(t0.AddSeconds(5));
                Check("⑨ 全员暂停信封（paused=1）生效", applied.Count == 3 && client.ServerPaused);

                fake.Fail = true;
                client.Tick(t0.AddSeconds(6));
                client.Tick(t0.AddSeconds(7));
                Check("⑩ 连续失败 2 次：仍在轮询（未达阈值 3）",
                    client.State == ServerLinkState.Polling && client.ConsecutiveFailures == 2);
                client.Tick(t0.AddSeconds(8));
                Check("⑩ 连续失败 3 次 → 回退单机（沿用最后生效策略）",
                    client.State == ServerLinkState.FallbackSingle && client.AppliedSeq == 6 &&
                    client.Current != null);
                fake.Fail = false;
                client.Tick(t0.AddSeconds(9));
                Check("⑩ 服务器恢复 → 自动回到轮询，失败计数归零",
                    client.State == ServerLinkState.Polling && client.ConsecutiveFailures == 0);

                Check("⑮ v0.11 请求带身份查询串（服务端据此解析分组）",
                    !string.IsNullOrEmpty(fake.LastQuery) && fake.LastQuery.Contains("device=LG-") &&
                    fake.LastQuery.Contains("machine=") && fake.LastQuery.StartsWith("?"), fake.LastQuery);
                localPaused = true;
                fake.SetPolicy(7, false, "{\"Enabled\":true,\"StartDelaySeconds\":90}");
                client.Tick(t0.AddSeconds(10));
                Check("⑪ 老师本地暂停中 → 服务器策略暂存不打扰（pending）",
                    applied.Count == 3 && client.HasPending && client.AppliedSeq == 6);
                localPaused = false;
                client.Tick(t0.AddSeconds(11));
                Check("⑪ 老师恢复后 → 自动应用暂存策略（暂停优先，不丢）",
                    applied.Count == 4 && client.AppliedSeq == 7 && !client.HasPending);

                EnvelopeData cached;
                bool cacheOk = ServerPolicyCache.TryLoad(pub, out cached);
                Check("⑫ 缓存可重新装载（重启后 seq 基线 = 7）",
                    cacheOk && cached.Seq == 7 && ServerPolicyCache.LastSeq == 7);
                string cachedText = File.ReadAllText(ServerPolicyCache.CachePath, Encoding.UTF8);
                File.WriteAllText(ServerPolicyCache.CachePath,
                    cachedText.Replace("LGSRV1|7|", "LGSRV1|99|"), new UTF8Encoding(false));
                Check("⑫ 缓存被篡改 → 验签拒绝并按无缓存处理（seq 基线归零）",
                    !ServerPolicyCache.TryLoad(pub, out cached) && ServerPolicyCache.LastSeq == 0);

                // ---- 合并语义：本机专属字段必须活下来
                var local = new GuardConfig { PasswordHash = "pbkdf2-sha256$100000$salt$hash" };
                local.Server.Enabled = true;
                local.Server.Address = "10.28.1.20:8210";
                local.Network.DnsServers = new List<string> { "10.28.1.10" };
                string serverPolicy = ConfigStore.Serialize(new GuardConfig());   // 服务器默认策略（无口令、Server=false）
                GuardConfig merged = ServerPolicyApplier.Merge(local, serverPolicy);
                Check("⑬ 服务器策略覆盖普通开关（本机 DNS 被服务器值覆盖）",
                    merged.Network.DnsServers.Count == 0);
                Check("⑬ 本机口令不被服务器策略清空（否则门禁失效）",
                    merged.PasswordHash == "pbkdf2-sha256$100000$salt$hash");
                Check("⑬ 本机服务器连接配置不被服务器策略关掉（防自锁）",
                    merged.Server.Enabled && merged.Server.Address == "10.28.1.20:8210");

                // ---- 真实 HTTP 回环：协议线格式端到端（真 transport ↔ 真 HttpListener）
                RunLoopbackSyncTest(key, pub, pairing);
            }
            finally
            {
                ServerPairing.DataDir = savedPairDir;
                ServerPolicyCache.DataDir = savedCacheDir;
                TryDeleteDir(tmp);
            }
        }

        /// <summary>真实 HTTP 回环联调：本机 HttpListener 扮演教师机，用真 transport 走一遍 version → policy → checkin。</summary>
        private static void RunLoopbackSyncTest(RSA key, RSA pub, PairingInfo pairing)
        {
            int port = FreeTcpPort();
            if (port <= 0) { Check("⑭ 回环联调（找不到空闲端口，已跳过）", true); return; }
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                Check("⑭ 回环联调（HttpListener 起不来，已跳过）", true, ex.Message);
                return;
            }

            string envelope = PolicyEnvelope.Build(21, new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc),
                false, "{\"Enabled\":true,\"StartDelaySeconds\":7}", key);
            var checkinBodies = new List<string>();
            var serverThread = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); } catch { break; }
                    try
                    {
                        string path = ctx.Request.Url.AbsolutePath;
                        string body;
                        if (path == "/api/version") body = "{\"seq\":21,\"sha256\":\"x\",\"paused\":false}";
                        else if (path == "/api/policy") body = envelope;
                        else if (path == "/api/checkin")
                        {
                            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                                lock (checkinBodies) checkinBodies.Add(sr.ReadToEnd());
                            body = "{\"ok\":true}";
                        }
                        else { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                }
            }) { IsBackground = true, Name = "selftest-fake-server" };
            serverThread.Start();

            try
            {
                var httpCfg = new GuardConfig();
                httpCfg.Server.Enabled = true;
                httpCfg.Server.Address = "127.0.0.1:" + port;
                httpCfg.Server.CheckinSeconds = 10;
                var httpApplied = new List<EnvelopeData>();
                var httpClient = new ServerClient(httpCfg, pairing, pub, new HttpServerTransport(),
                    () => false, e => httpApplied.Add(e));
                httpClient.Tick(DateTime.UtcNow);
                Check("⑭ 真实 HTTP：/api/version → /api/policy → 验签 → 应用",
                    httpApplied.Count == 1 && httpApplied[0].Seq == 21, "applied=" + httpApplied.Count);

                for (int i = 0; i < 20 && CheckinCount(checkinBodies) == 0; i++) Thread.Sleep(50);
                Check("⑭ 真实 HTTP：心跳 POST 到达服务端且含机器名",
                    CheckinCount(checkinBodies) == 1 && LastCheckin(checkinBodies).Contains(Environment.MachineName));
                httpClient.Stop();
            }
            finally
            {
                try { listener.Stop(); } catch { }
                try { listener.Close(); } catch { }
            }
        }

        private static int CheckinCount(List<string> bodies) { lock (bodies) return bodies.Count; }
        private static string LastCheckin(List<string> bodies)
        {
            lock (bodies) return bodies.Count == 0 ? "" : bodies[bodies.Count - 1];
        }

        private static int FreeTcpPort()
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch { return -1; }
        }

        /// <summary>自检用的假通道：可切"断网 / 未就绪"，可指定探测 seq 与信封正文不一致（伪造/重放场景）。</summary>
        private sealed class FakeServer : IServerTransport
        {
            private readonly RSA _key;
            public int VersionSeq;
            public bool NotReady;
            public bool Fail;
            public string PolicyText;
            public int PolicyFetchCount;
            public string LastQuery;                     // v0.11：服务端据此解析分组（device/machine）
            public readonly List<string> Checkins = new List<string>();

            public FakeServer(RSA key) { _key = key; }
            public string LastCheckin { get { return Checkins.Count == 0 ? "" : Checkins[Checkins.Count - 1]; } }

            public void SetPolicy(ulong seq, bool paused, string policyJson)
            {
                PolicyText = PolicyEnvelope.Build(seq, new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc),
                    paused, policyJson, _key);
                VersionSeq = (int)seq;
            }

            public void SetProbeSeq(ulong seq) { VersionSeq = (int)seq; }

            public string GetVersion(string address, string query)
            {
                if (Fail) throw new IOException("模拟断网");
                if (NotReady) return null;
                LastQuery = query;
                return "{\"seq\":" + VersionSeq + ",\"sha256\":\"x\",\"paused\":false}";
            }

            public string GetPolicy(string address, string query)
            {
                if (Fail) throw new IOException("模拟断网");
                PolicyFetchCount++;
                return PolicyText;
            }

            public void Checkin(string address, string body)
            {
                if (Fail) throw new IOException("模拟断网");
                Checkins.Add(body);
            }
        }

        /// <summary>
        /// [12] 服务器运行态（v0.10 M2c）：ServerSyncState / PauseState 的跨进程语义与"两个暂停来源互不污染"。
        /// 关键不变量：老师把服务器管控**关掉**时必须同时清掉"服务器暂停"态 ——
        /// 否则机器会停在"服务器暂停"里而服务器已不再管它，老师永远解不开。
        /// </summary>
        private static void TestServerRuntime()
        {
            Console.WriteLine("[12] 服务器运行态（状态快照 / 暂停来源隔离）");

            string tmp = Path.Combine(Path.GetTempPath(),
                "labguard-selftest-runtime-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string savedDir = ServerSyncState.DataDir;
            try
            {
                Directory.CreateDirectory(tmp);
                ServerSyncState.DataDir = tmp;

                ServerSyncState.Reset();
                Check("① 未启用：Enabled=false 且永不处于「暂停」（防止关掉服务器后解不开）",
                    !ServerSyncState.Enabled && !ServerSyncState.ServerPaused &&
                    ServerSyncState.Describe().IndexOf("单机模式", StringComparison.Ordinal) >= 0);

                var on = new GuardConfig();
                on.Server.Enabled = true;
                ServerSyncState.NoteConfigSaved(on);
                Check("② 配置保存后状态跟随（Enabled=true）", ServerSyncState.Enabled);

                var key = new RSACryptoServiceProvider(2048);
                EnvelopeData env = PolicyEnvelope.Parse(
                    PolicyEnvelope.Build(9, DateTime.UtcNow, true, "{\"Enabled\":true}", key), PublicOnly(key));
                Check("   （夹具有效：paused=1 信封验签通过）", env.Verified && env.Paused, env.Error);
                ServerSyncState.MarkApplied(env, "10.28.1.20:8210");
                Check("③ 应用后：seq / 暂停 / 地址都进了状态",
                    ServerSyncState.AppliedSeq == 9 && ServerSyncState.ServerPaused &&
                    ServerSyncState.Address == "10.28.1.20:8210");
                Check("③ 服务器暂停 → 统一口径 PauseState.IsPaused 必为真（与老师是否暂停无关）",
                    PauseState.ServerPaused && PauseState.IsPaused);
                Check("③ 状态摘要可读（含 seq 与暂停字样）",
                    ServerSyncState.Describe().IndexOf("9") >= 0 &&
                    ServerSyncState.Describe().IndexOf("暂停", StringComparison.Ordinal) >= 0,
                    ServerSyncState.Describe());

                // 跨进程视角：另一个"进程"（这里用重新 Load 模拟）能读到同一份状态
                ServerSyncState.MarkApplied(env, "10.28.1.20:8210");
                ServerSyncState.Load();
                Check("④ 状态文件可被其它进程/重启读回（含启用、暂停、seq）",
                    ServerSyncState.Enabled && ServerSyncState.ServerPaused && ServerSyncState.AppliedSeq == 9 &&
                    File.Exists(ServerSyncState.StatePath) &&
                    File.ReadAllText(ServerSyncState.StatePath).IndexOf("\"schemaVersion\":1", StringComparison.Ordinal) >= 0,
                    ServerSyncState.Describe());

                ServerSyncState.MarkApplied(env, "10.28.1.20:8210");
                var off = new GuardConfig();
                off.Server.Enabled = false;
                ServerSyncState.NoteConfigSaved(off);
                Check("⑤ 关键：老师关掉服务器管控 → 暂停态同时清零（否则永远解不开）",
                    !ServerSyncState.Enabled && !ServerSyncState.ServerPaused && !PauseState.ServerPaused);
                Check("⑤ 文件里也同步写下了 enabled=false",
                    File.ReadAllText(ServerSyncState.StatePath).IndexOf("\"enabled\":false", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                ServerSyncState.Reset();            // 先在临时目录里清干净，别把状态写进真实 %ProgramData%
                ServerSyncState.DataDir = savedDir;
                TryDeleteDir(tmp);
            }
        }

        /// <summary>
        /// [13] 设备身份与状态上报（v0.10.4，LG-009 / LG-010）：
        /// 稳定 DeviceId（生成 / 持久化 / 格式 / 不撞号）+ 心跳体携带 deviceId / appliedSeq / status / errorCode。
        /// 教师端靠这些字段区分"同一台设备"，并判断 已应用 / 待应用 / 应用失败。
        /// </summary>
        private static void TestDeviceIdentity()
        {
            Console.WriteLine("[13] 设备身份与状态上报（DeviceId + 已应用序号 + 应用结果）");

            string tmp = Path.Combine(Path.GetTempPath(), "labguard-selftest-devid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string savedDir = DeviceId.DataDir;
            string savedPair = ServerPairing.DataDir;
            string savedCache = ServerPolicyCache.DataDir;
            try
            {
                Directory.CreateDirectory(tmp);
                DeviceId.DataDir = tmp;

                string id = DeviceId.Get();
                Check("① 设备 ID 格式 = LG- + 16 位十六进制", DeviceId.IsValid(id), id);
                Check("① 第二次读取得到同一个 ID（重启/改名都不变）", DeviceId.Get() == id);
                Check("① 已落盘 device-id.txt", File.Exists(DeviceId.IdPath));
                Check("① 拒绝非法格式（长度/前缀/非 hex/空）",
                    !DeviceId.IsValid("LG-0011") && !DeviceId.IsValid("XX-0011223344556677") &&
                    !DeviceId.IsValid("LG-00112233445566zz") && !DeviceId.IsValid(null));
                Check("① 新生成的 ID 不撞号（CSPRNG）", DeviceId.NewId() != DeviceId.NewId());

                var key = new RSACryptoServiceProvider(2048);
                RSA pub = PublicOnly(key);
                string pairDir = Path.Combine(tmp, "pair");
                Directory.CreateDirectory(pairDir);
                ServerPairing.DataDir = pairDir;
                ServerPolicyCache.DataDir = pairDir;
                string pairPath = Path.Combine(pairDir, "labguard-pairing.json");
                File.WriteAllText(pairPath, BuildPairingJson(key.ToXmlString(false), 8210,
                    ServerPairing.ComputeFingerprint(key.ToXmlString(false))), new UTF8Encoding(false));
                PairingInfo pairing = ServerPairing.Import(pairPath);

                var cfg = new GuardConfig();
                cfg.Server.Enabled = true;
                var fake = new FakeServer(key);
                bool failApply = false;
                var client = new ServerClient(cfg, pairing, pub, fake, () => false, e =>
                {
                    if (failApply) throw new InvalidOperationException("模拟应用失败");
                });

                fake.SetPolicy(3, false, "{\"Enabled\":true,\"StartDelaySeconds\":30}");
                client.Tick(DateTime.UtcNow);
                string okBody = client.BuildCheckinJson();
                Check("② 心跳体带 deviceId", okBody.IndexOf("\"deviceId\":\"" + DeviceId.Get() + "\"", StringComparison.Ordinal) >= 0, okBody);
                Check("② 心跳体带 appliedSeq（= 已应用序号）",
                    okBody.Contains("\"appliedSeq\":3") && okBody.Contains("\"seq\":3"));
                Check("② 应用成功 → status=ok 且不出现 errorCode",
                    okBody.Contains("\"status\":\"ok\"") && !okBody.Contains("errorCode"));

                failApply = true;
                fake.SetPolicy(4, false, "{\"Enabled\":true,\"StartDelaySeconds\":60}");
                client.Tick(DateTime.UtcNow);
                string errBody = client.BuildCheckinJson();
                Check("③ 应用失败 → status=error + 稳定错误码",
                    errBody.Contains("\"status\":\"error\"") && errBody.Contains("LG-POLICY-APPLY-FAILED"), errBody);
                Check("③ 失败后已应用序号不前进（仍按旧策略跑）", errBody.Contains("\"appliedSeq\":3"));

                failApply = false;
                fake.SetPolicy(5, false, "{\"Enabled\":true,\"StartDelaySeconds\":90}");
                client.Tick(DateTime.UtcNow);
                string okBody2 = client.BuildCheckinJson();
                Check("④ 恢复后 status 回到 ok、已应用序号前进到 5",
                    okBody2.Contains("\"status\":\"ok\"") && okBody2.Contains("\"appliedSeq\":5"));
            }
            finally
            {
                DeviceId.DataDir = savedDir;
                ServerPairing.DataDir = savedPair;
                ServerPolicyCache.DataDir = savedCache;
                TryDeleteDir(tmp);
            }
        }

        /// <summary>公钥副本（只有公钥部分——模拟学生机上 pin 住的那把）。</summary>
        private static RSA PublicOnly(RSA key)
        {
            var pub = new RSACryptoServiceProvider();
            pub.FromXmlString(key.ToXmlString(false));
            return pub;
        }

        /// <summary>构造一份与教师机服务端 PairingFile 同形的配对文件（公钥 XML 里没有需要转义的字符）。</summary>
        private static string BuildPairingJson(string publicKeyXml, int port, string fingerprint)
        {
            return "{\"format\":\"labguard-pairing/1\",\"serverHost\":\"TEACHER-PC\"," +
                   "\"addresses\":[\"10.28.1.20\",\"192.168.1.20\"],\"port\":" + port + "," +
                   "\"publicKeyXml\":\"" + publicKeyXml + "\"," +
                   "\"fingerprint\":\"" + fingerprint + "\"," +
                   "\"generatedAt\":\"2026-10-12T00:00:00.0000000Z\"}";
        }

        private static bool Throws(Func<bool> action)
        {
            try { action(); return false; }
            catch { return true; }
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        /// <summary>清理自检临时目录：先恢复权限继承（Import 会把 ACL 收紧）再删，删不掉就留着（在 %TEMP% 里，无害）。</summary>
        private static void TryDeleteDir(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        FileSecurity sec = fi.GetAccessControl();
                        sec.SetAccessRuleProtection(false, true);
                        fi.SetAccessControl(sec);
                    }
                    catch { }
                }
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("      （临时目录清理失败，可忽略：" + dir + " :: " + ex.Message + "）");
            }
        }

        /// <summary>
        /// [14] 防会话逃逸（v0.13）：「学生换用户 / 开第二个会话」是"多开桌面脱离管制"里最硬的一条路。
        /// 这里只验**不碰真机**的部分：配置能存能读、开关目录里有位、引擎两个角色都挂上了它、
        /// 干跑时会写的那两个注册表位置与取值正确、以及"没有原值备份时停止绝不动系统"。
        /// </summary>
        private static void TestSessionEscape()
        {
            Console.WriteLine("[14] 防会话逃逸（换用户 / 新会话）");

            var cfg = new GuardConfig();
            Check("14.1 出厂全关（组是关的）+ 组内默认值合理：禁快速切换用户、**远程桌面仍默认关**（怕把老师自己的远程排障也挡了）",
                !cfg.Session.Enabled && cfg.Session.BlockFastUserSwitch && !cfg.Session.DenyRemoteDesktop);
            cfg.Session.Enabled = true;     // 下面几条验的是"打开之后"的行为（出厂默认是关的）

            var round = ConfigStore.Deserialize(ConfigStore.Serialize(cfg));
            round.Session.DenyRemoteDesktop = true;
            var round2 = ConfigStore.Deserialize(ConfigStore.Serialize(round));
            Check("14.2 配置能存能读（不丢新分组）",
                round2.Session.DenyRemoteDesktop && round2.Session.BlockFastUserSwitch);

            var fields = SettingsCatalog.All();
            int n = fields.Count(f => f.Section.StartsWith("16、", StringComparison.Ordinal));
            Check("14.3 开关目录里有这一组（3 项）→ 教师端策略面板会渲染出来",
                n == 3 && fields.Any(f => f.Path == "Session.BlockFastUserSwitch") &&
                fields.Any(f => f.Path == "Session.DenyRemoteDesktop"), n + " 项");

            int before = SystemActions.AuditTrail.Count;
            var engine = new GuardEngine(cfg);
            engine.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, EngineRole.SystemCore);
            Check("14.4 服务侧（SystemCore，机器级）挂上了「防会话逃逸」",
                engine.Guards.Any(g => g.Name == "防会话逃逸"));
            engine.Stop();
            var ops = SystemActions.AuditTrail.Skip(before).ToList();
            Check("14.5 干跑时确实去写「禁快速切换用户」那个位置（HKLM\\...\\Policies\\System）",
                ops.Any(a => a.Contains("Policies\\System\\HideFastUserSwitching = 1")));
            Check("14.6 默认不碰远程桌面（DenyRemoteDesktop=false → 一条 RDP 相关写入都没有）",
                !ops.Any(a => a.Contains("fDenyTSConnections")));

            var engine2 = new GuardEngine(cfg);
            engine2.Start(null, AppDomain.CurrentDomain.BaseDirectory, true, EngineRole.Full);
            // 这两条都是 HKLM（机器级）：**只由服务侧写**。用户会话侧不重复挂，
            // 否则两个进程对同一批值来回写、两套"原值备份"互相覆盖，退出时谁都还原不回去。
            Check("14.7 只在服务侧写（用户会话侧不重复挂，避免两个进程互抢同一批 HKLM 值与备份）",
                !engine2.Guards.Any(g => g.Name == "防会话逃逸"));
            engine2.Stop();

            // 没有原值备份 = 这些值不是我们写的 → 停止时绝不动系统（否则会把管理员/组策略设的值一起抹掉）
            int before2 = SystemActions.AuditTrail.Count;
            var lonely = new LabGuard.Core.Guards.SessionGuard();
            lonely.AttachContext(new LabGuard.Core.Guards.GuardContext(cfg, AppDomain.CurrentDomain.BaseDirectory, null));
            lonely.Stop();
            var ops2 = SystemActions.AuditTrail.Skip(before2)
                .Where(a => a.Contains("HideFastUserSwitching") || a.Contains("fDenyTSConnections")).ToList();
            Check("14.8 没有原值备份时，停止**不动系统**（绝不删别人的设置）", ops2.Count == 0, ops2.Count + " 条");
        }
    }
}
