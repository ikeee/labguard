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
                    () => { });
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
                try { p.Kill(); } catch { }
            }
            LabGuard.Core.Interop.SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName);
            MessageBox.Show("已暂停监控并解除锁定。\r\n\r\n要恢复监控：运行【设置】后保存，或再次运行小助手。",
                "LabGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
