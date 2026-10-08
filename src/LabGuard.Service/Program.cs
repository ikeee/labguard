using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Service
{
    internal static class Program
    {
        /// <summary>
        /// 守护服务（zmserv.exe）。以 SYSTEM 运行，负责系统级策略，
        /// 并保证用户会话里的 LabGuard.Agent.exe 一直活着。
        /// 用法：直接运行 = 控制台调试模式；由 SCM 启动 = 服务模式。
        /// </summary>
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--console")
            {
                Log.EchoToConsole = true;
                Log.MinLevel = LogLevel.Debug;
                using (var svc = new GuardService())
                {
                    svc.DebugStart();
                    Console.WriteLine("按回车停止...");
                    Console.ReadLine();
                    svc.DebugStop();
                }
                return;
            }

            // 兜底自愈（一次性）：由 SYSTEM 计划任务「LabGuard\Guard」每分钟调用。
            // 服务被删/被停 → 重新注册并启动；小助手不在 → 拉起。详见 GuardService.EnsureOnce。
            if (args != null && args.Length > 0 && args[0] == "--ensure")
            {
                Log.EchoToConsole = true;
                int handled = GuardService.EnsureOnce();
                Console.WriteLine("兜底自愈完成：本次处理了 " + handled + " 项。");
                return;
            }

            // 恢复监控（一次性）：清掉"老师已暂停"标记。
            // 后路：暂停期间小助手若被杀，老师可能连托盘都没有（点不到"启动监控"），用它一键恢复。
            if (args != null && args.Length > 0 && args[0] == "--resume")
            {
                Log.EchoToConsole = true;
                string rep;
                int rc = GuardService.ResumeAll(out rep);
                Console.WriteLine(rep);
                Environment.ExitCode = rc;
                return;
            }

            // 演练（一次性）：验证"老师暂停监控期间，学生杀掉小助手还会不会被拉起"。
            // 2026-10-08 真机事故的回归入口：那次杀掉后 8 小时没人管。退出码 0=PASS / 1=FAIL / 2=不适用。
            if (args != null && args.Length > 0 && args[0] == "--pause-drill")
            {
                Log.EchoToConsole = true;
                int seconds = 20;
                if (args.Length > 1) int.TryParse(args[1], out seconds);
                string report;
                int rc = GuardService.PauseDrill(seconds, out report);
                Console.WriteLine("暂停期防拆演练：" + report);
                Environment.ExitCode = rc;
                return;
            }

            // 文件自愈（一次性）：被删/被改的程序文件从系统级备份副本恢复。安装程序与排障都能用。
            if (args != null && args.Length > 0 && args[0] == "--restore-files")
            {
                string dir = args.Length > 1 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;
                int n = LabGuard.Core.Guards.WatchdogGuard.RestoreFromPayload(dir, null, null);
                Console.WriteLine("已恢复 " + n + " 个文件（备份副本：" + LabGuard.Core.Guards.WatchdogGuard.PayloadDir + "）");
                return;
            }

            if (!Environment.UserInteractive)
            {
                ServiceBase.Run(new ServiceBase[] { new GuardService() });
                return;
            }

            // 被用户双击：给出提示，避免误以为程序坏了
            Console.WriteLine("这是后台服务程序，请用 install.ps1 安装，或用 --console 调试。");
        }
    }
}
