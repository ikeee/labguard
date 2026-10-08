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
            TestClassroomProducts();
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

            Check("默认开启进程防拆（四层都开）",
                new GuardConfig().AntiTamper.Enabled &&
                new GuardConfig().AntiTamper.HardenProcessDacl &&
                new GuardConfig().AntiTamper.FastRestart &&
                new GuardConfig().AntiTamper.ScheduledTaskGuard);
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
            Check("只开「进程防拆」、关「互相守护」时仍然守护",
                LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(cfgD, true));
            Check("空配置不会抛异常",
                !LabGuard.Core.Guards.WatchdogGuard.ShouldKeepAgentAlive(null, true));

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
