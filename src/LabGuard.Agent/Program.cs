using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LabGuard.Core;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Logging;

namespace LabGuard.Agent
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 兜底：任何异常都写崩溃日志 + 弹提示，避免"双击一下就没反应/闪退"
            LabGuard.Core.CrashHandler.Install("LabGuard.Agent");
            LabGuard.Core.CrashHandler.Run("LabGuard.Agent", () => MainCore(args));
        }

        private static void MainCore(string[] args)
        {
            bool dryRun = HasArg(args, "--dry-run") || HasArg(args, "/dryrun");
            Log.EchoToConsole = HasArg(args, "--console");
            Log.MinLevel = dryRun ? LogLevel.Debug : LogLevel.Info;

            GuardConfig config = ConfigStore.Load();

            if (HasArg(args, "--status"))
            {
                foreach (var kv in config.Snapshot())
                    Console.WriteLine("{0,-16} {1}", kv.Key, kv.Value ? "启用" : "禁用");
                Console.WriteLine("已安装：" + ConfigStore.IsInstalled());
                return;
            }

            // --unlock：老师用密码"解除锁定/暂停监控"（可从快捷方式一键调用）
            if (HasArg(args, "--unlock"))
            {
                UnlockByPassword(config);
                return;
            }

            // --preview-mask [秒]：预览"断网遮罩"长什么样（只显示几秒，自动关闭，不动网络）
            if (HasArg(args, "--preview-mask"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                int seconds = 8;
                int idx = Array.IndexOf(args, "--preview-mask");
                if (args.Length > idx + 1) int.TryParse(args[idx + 1], out seconds);
                DateTime deadline = DateTime.Now.AddSeconds(Math.Max(3, seconds));
                var cfg = ConfigStore.Load();
                var mask = new LabGuard.Core.UI.DisconnectMaskForm();
                mask.ShowMask("机位 DEMO-01", "网络已断开（预览）", "请插回网线或启用网络连接",
                    string.IsNullOrEmpty(cfg.PasswordHash) ? PasswordHasher.Create("a1b2c3") : cfg.PasswordHash,
                    true, true,
                    () =>
                    {
                        TimeSpan span = deadline - DateTime.Now;
                        return (span.TotalSeconds > 0 ? "预览将在 " + (int)span.TotalSeconds + " 秒后自动关闭" : "即将自动关闭")
                               + " · 真实场景：插回网线后 10 秒内自动恢复";
                    },
                    () => DateTime.Now >= deadline,
                    channel => { });
                // 自己泵消息：遮罩按"网络已恢复"条件自动隐藏，这里等到截止时间后收尾
                while (DateTime.Now < deadline.AddSeconds(1))
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }
                mask.AutoClose("预览结束");
                mask.Close();
                mask.Dispose();
                Console.WriteLine("遮罩预览结束：遮罩已按条件自动解除，键盘钩子已卸载。");
                return;
            }

            // --unlock-drill：**老师解锁演练** —— 不用真的拔网线也能验证这条救命通道。
            // 自动连按 5 次 Esc 弹出密码框 → 输入口令 → 回车，最后断言遮罩是不是真的被老师解除。
            // 之所以要有它：这条路径由「全局键盘钩子 + InputLock + 模态对话框」三样东西叠成，
            // 只靠读代码看不出问题（历史 bug：钩子没放行，老师看得见密码框却打不出字）。
            if (HasArg(args, "--unlock-drill"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                int dIdx = Array.IndexOf(args, "--unlock-drill");
                string drillPassword = (args.Length > dIdx + 1 && !args[dIdx + 1].StartsWith("-"))
                    ? args[dIdx + 1] : "a1b2c3";
                bool teacherUnlocked = false;
                bool gestureMode = HasArg(args, "--gesture");
                bool noLock = HasArg(args, "--no-lock");

                var mask = new LabGuard.Core.UI.DisconnectMaskForm();
                // 手势演练**默认真锁键鼠**：正因为 BlockInput 期间低级键盘钩子照常工作（见 docs/06），
                // 手势才能在硬锁下解锁——这是本方案相对密码框方案的核心优势，就该在演练里被验到。
                // （兜底：InputLock 有 10 秒看门狗，本演练也有 60 秒超时自动收尾。）
                mask.InputLockMode = noLock ? LabGuard.Core.Interop.InputLock.ModeOff
                                            : LabGuard.Core.Interop.InputLock.ModeOn;
                mask.TeacherUnlockMode = gestureMode ? "Gesture" : "Password";
                string how = gestureMode
                    ? "按 Pause → ↑↑↓↓←→←→"
                    : "连按 5 次 Esc → 输入口令 " + drillPassword + " → 回车";
                mask.ShowMask("机位 DRILL-01", "网络已断开（演练）",
                    "老师解锁演练：" + how + "。",
                    PasswordHasher.Create(drillPassword), false, true,
                    () => "演练中：" + how,
                    () => false,                                               // 演练不靠网络恢复
                    channel => { teacherUnlocked = true; });                    // 老师途径解锁时置位（channel：手势/密码）

                Console.WriteLine("等待老师操作：" + how + "（60 秒内，"
                    + (noLock ? "未锁键鼠" : "已硬锁键鼠") + "）。");

                // 按键由**操作者（或测试脚本）**从外部敲 —— 和真实老师完全一致：
                // 键盘钩子是全局的，不论谁敲都会被它拦到；而由本进程注入会被系统节流/延迟。
                DateTime end = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < end)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                    if (!mask.Visible || teacherUnlocked) break;
                }

                if (mask.Visible) mask.AutoClose("演练超时");
                bool closed = !mask.Visible;
                bool hookGone = !mask.IsHookInstalled;
                string verdict =
                    (teacherUnlocked
                        ? (gestureMode ? "手势已命中，老师已解除遮罩" : "密码框可见且能输入，老师已解除遮罩")
                        : (gestureMode ? "未能用手势解锁（FAIL：序列没被识别？）" : "未能用口令解锁（FAIL：字打不进密码框？）"))
                    + "；" + (closed ? "遮罩已关闭" : "遮罩仍可见（FAIL）")
                    + "；" + (hookGone ? "键盘钩子已卸载" : "键盘钩子仍在（FAIL：之后 Win/Alt+Tab 会被吞）")
                    + ((teacherUnlocked && closed && hookGone) ? "（PASS）" : "（FAIL）");
                Console.WriteLine("老师解锁演练结束：" + verdict);
                Log.Info("[解锁演练] " + verdict);
                mask.Dispose();
                Environment.ExitCode = (teacherUnlocked && closed && hookGone) ? 0 : 1;
                return;
            }

            // --preview-lock [秒]：预览"全屏锁定屏"长什么样（只显示几秒，条件到点自动满足后解除；不锁键鼠）
            if (HasArg(args, "--preview-lock"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                int seconds = 8;
                int idx = Array.IndexOf(args, "--preview-lock");
                if (args.Length > idx + 1) int.TryParse(args[idx + 1], out seconds);
                DateTime deadline = DateTime.Now.AddSeconds(Math.Max(3, seconds));
                var cfg = ConfigStore.Load();

                int ticks = 0;      // 看门狗定时器实际触发次数：验证"定时器真的在走"（线程归属修复的核心）
                var lockForm = new LabGuard.Core.UI.LockScreenForm(
                    string.IsNullOrEmpty(cfg.PasswordHash) ? PasswordHasher.Create("a1b2c3") : cfg.PasswordHash);
                lockForm.InputLockMode = LabGuard.Core.Interop.InputLock.ModeOff;   // 预览不锁键鼠，随时可退出
                lockForm.ShowLock("已锁定（预览）", "检测到违规：这是一条示例原因。\r\n条件到点会自动满足，无需输密码。",
                    false, () => { ticks++; return DateTime.Now >= deadline; });

                while (DateTime.Now < deadline.AddSeconds(5))
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                    if (!lockForm.Visible) break;   // 已自动解除，提前收工
                }

                bool autoClosed = !lockForm.Visible;
                bool hookGone = !lockForm.IsHookInstalled;      // 解锁后全局键盘钩子必须已卸掉
                string verdict = "看门狗定时器触发 " + ticks + " 次，" +
                    (autoClosed ? "已按条件自动解除" : "未能自动解除（FAIL：定时器没走）") + "；" +
                    (hookGone ? "键盘钩子已卸载" : "键盘钩子仍在（FAIL：解锁后 Win/Alt+Tab 会被吞）") +
                    ((autoClosed && ticks > 0 && hookGone) ? "（PASS）" : "（FAIL）");
                Console.WriteLine("锁定屏预览结束：" + verdict);
                Log.Info("[锁定屏预览] " + verdict);
                lockForm.Dispose();
                Environment.ExitCode = (autoClosed && ticks > 0 && hookGone) ? 0 : 1;
                return;
            }

            // --harden-drill：**进程防拆演练**（可断言，不用肉眼看截图）
            // 验证"加固 → 任务管理器杀不掉 → 老师仍能正常结束"这条链：
            // ① 套 DACL 后读回来校验形态；② 用当前身份试 OpenProcess(TERMINATE/VM_WRITE)；
            // ③ 还原成默认 DACL 后再试一次（必须能杀，否则老师/卸载脚本会被自己挡住）。
            if (HasArg(args, "--harden-drill"))
            {
                bool keep = HasArg(args, "--keep");
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                bool admin = LabGuard.Core.Interop.ProcessHardening.IsAdministrator();
                string sid = LabGuard.Core.Interop.ProcessHardening.CurrentUserSid();
                Console.WriteLine("进程防拆演练（pid=" + pid + "；管理员身份=" + (admin ? "是" : "否") + "）");

                string detail;
                bool ok = LabGuard.Core.Interop.ProcessHardening.HardenSelf(out detail);
                string after = LabGuard.Core.Interop.ProcessHardening.ReadOwnSddl();
                bool looks = LabGuard.Core.Interop.ProcessHardening.LooksHardened(after, sid);
                bool canKill = LabGuard.Core.Interop.ProcessHardening.CanOpen(pid, LabGuard.Core.Interop.ProcessHardening.Terminate);
                bool canWrite = LabGuard.Core.Interop.ProcessHardening.CanOpen(pid, LabGuard.Core.Interop.ProcessHardening.VmWrite);

                Console.WriteLine("  加固调用：" + (ok ? "成功" : "失败 → " + detail));
                Console.WriteLine("  加固后 DACL：" + after);
                Console.WriteLine("  形态校验（SYSTEM/管理员完全控制 + 当前用户只剩查询权）：" + (looks ? "通过" : "未通过"));
                Console.WriteLine("  当前身份 OpenProcess(TERMINATE)：" + (canKill ? "成功（杀得掉）" : "被拒绝（杀不掉）"));
                Console.WriteLine("  当前身份 OpenProcess(VM_WRITE)：" + (canWrite ? "成功" : "被拒绝"));

                string how;
                bool pass;
                if (admin)
                {
                    // 管理员（含 SeDebugPrivilege）本来就绕得过 DACL —— 这是设计上承认的边界，
                    // 管理员机房靠"秒级复活 + 兜底计划任务"，不靠这一层挡。
                    how = "管理员身份：DACL 已按预期套上，但管理员仍可结束进程（设计如此，见 docs/07）；" +
                          "这台机器防的是「杀掉后不回来」，由秒级复活与兜底任务兜住";
                    pass = looks;
                }
                else
                {
                    pass = looks && !canKill && !canWrite;
                    how = pass ? "标准用户：任务管理器 / taskkill 已无法结束本进程（拒绝访问）"
                               : "标准用户仍能结束本进程（FAIL）";
                }

                bool restored = true;
                bool canKillAfter = true;
                if (!keep)
                {
                    restored = LabGuard.Core.Interop.ProcessHardening.RestoreSelf(out detail);
                    canKillAfter = LabGuard.Core.Interop.ProcessHardening.CanOpen(pid, LabGuard.Core.Interop.ProcessHardening.Terminate);
                    Console.WriteLine("  还原 DACL：" + (restored ? "成功" : "失败 → " + detail));
                    Console.WriteLine("  还原后 OpenProcess(TERMINATE)：" + (canKillAfter ? "成功（老师/卸载脚本能正常结束）" : "仍被拒绝（FAIL：会把自己挡在门外）"));
                    pass = pass && restored && canKillAfter;
                }

                string verdict = "进程防拆演练结束：" + how + "（" + (pass ? "PASS" : "FAIL") + "）";
                Console.WriteLine(verdict);
                Log.Info("[防拆演练] " + how + "（" + (pass ? "PASS" : "FAIL") + "）");
                // --out <文件>：把结论落盘。用 runas /trustlevel 以"标准用户"身份跑演练时，
                // 新窗口里的输出看不到，只能靠文件回收结论（本机是管理员，这是唯一能验"标准用户杀不掉"的办法）。
                int oi = Array.IndexOf(args, "--out");
                if (oi >= 0 && args.Length > oi + 1)
                {
                    try { File.WriteAllText(args[oi + 1], verdict, new System.Text.UTF8Encoding(false)); }
                    catch (Exception ex) { Console.WriteLine("写结果文件失败：" + ex.Message); }
                }
                Environment.ExitCode = pass ? 0 : 1;
                return;
            }

            // 已配置密码时才允许单实例；未配置时也允许运行（只是不启用管控）
            bool created;
            using (var mutex = new Mutex(true, @"Global\LabGuard.Agent", out created))
            {
                if (!created && !HasArg(args, "--force"))
                {
                    Log.Warn("小助手已在运行，忽略本次启动");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var context = new TrayApplicationContext(config, dryRun);
                Application.Run(context);
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string a in args ?? new string[0])
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void UnlockByPassword(GuardConfig config)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (string.IsNullOrEmpty(config.PasswordHash))
            {
                MessageBox.Show("尚未设置密码，无需解锁。", "LabGuard");
                return;
            }
            using (var dlg = new LabGuard.Core.UI.PasswordDialog("LabGuard - 解除锁定/暂停监控", "输入小助手密码：", config.PasswordHash))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
            }
            WatchdogGuard.MarkPaused(true);
            // 结束正在运行的小助手（它的锁屏窗口会随之关闭），并停掉守护服务；暂停期间不会被自动拉起
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("LabGuard.Agent"))
            {
                if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                try
                {
                    // 进程防拆加固后，"同用户"已经杀不掉它了 —— 先把它的 DACL 还原成默认再结束，
                    // 否则老师用密码暂停时会留下一个杀不掉的小助手（加固挡学生可以，绝不能挡老师）。
                    string detail;
                    LabGuard.Core.Interop.ProcessHardening.RestoreProcess(p.Id, out detail);
                    p.Kill();
                }
                catch { }
            }
            LabGuard.Core.Interop.SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName);
            MessageBox.Show("已暂停监控并解除锁定。\r\n\r\n要恢复监控：运行【设置】后保存，或再次运行小助手。",
                "LabGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
