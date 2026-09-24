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
        /// 守护服务（对应原版 zmserv.exe）。以 SYSTEM 运行，负责系统级策略，
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
