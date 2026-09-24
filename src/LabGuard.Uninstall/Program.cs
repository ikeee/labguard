using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using LabGuard.Core.UI;

namespace LabGuard.Uninstall
{
    /// <summary>
    /// 卸载程序（uninstall.exe）：
    /// 密码 → 停止服务与代理 → 逐项还原系统（hosts/USB/注册表策略/浏览器策略/IFEO/安全模式/壁纸）
    /// → 删除服务、计划任务、快捷方式、控制面板卸载项、配置 → 删程序目录 → 询问重启。
    ///
    /// 自删除：程序自己跑在安装目录里删不掉自己 → 复制一份到 %TEMP% 用 `--purge &lt;dir&gt;` 收尾。
    /// 参数：--force（免密码）--quiet（不弹确认，批量用）--keep-files（保留程序目录）--purge（内部）
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 收尾副本会被复制到 %TEMP% 运行，那里没有 LabGuard.Core.dll。
        /// 这里兜底解析：先找 exe 同目录 → 再找 --purge 指定的安装目录 → 最后查注册表 InstallDir。
        /// （没有它，副本会在 JIT 主函数时直接 FileNotFoundException 崩掉，目录一个文件都删不掉。）
        /// </summary>
        static Program()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                try
                {
                    if (!string.Equals(new System.Reflection.AssemblyName(e.Name).Name,
                            "LabGuard.Core", StringComparison.OrdinalIgnoreCase)) return null;

                    var candidates = new System.Collections.Generic.List<string>
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabGuard.Core.dll")
                    };
                    string[] cargs = Environment.GetCommandLineArgs();
                    for (int i = 0; i < cargs.Length - 1; i++)
                    {
                        if (string.Equals(cargs[i], "--purge", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(Path.Combine(cargs[i + 1], "LabGuard.Core.dll"));
                    }
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ConfigStore.RegistryRoot))
                        {
                            object dir = k?.GetValue("InstallDir");
                            if (dir != null) candidates.Add(Path.Combine(dir.ToString(), "LabGuard.Core.dll"));
                        }
                    }
                    catch { }

                    foreach (string c in candidates)
                    {
                        // 必须从内存加载：LoadFrom 会锁住文件，收尾副本就删不掉 LabGuard.Core.dll 了
                        if (File.Exists(c)) return System.Reflection.Assembly.Load(File.ReadAllBytes(c));
                    }
                }
                catch { }
                return null;
            };
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            args = args ?? new string[0];
            bool force = Has(args, "--force");
            bool quiet = Has(args, "--quiet");
            bool keepFiles = Has(args, "--keep-files");
            string givenPassword = Value(args, "--password");

            // ---- 收尾模式：等父进程退出后删整个目录 ----
            string purgeDir = Value(args, "--purge");
            if (!string.IsNullOrEmpty(purgeDir))
            {
                Purge(purgeDir);
                return;
            }

            GuardConfig config = ConfigStore.Load();
            // 免密只有一条路：显式 --force（老师忘记密码时的救援入口，不会写在任何脚本/文档下发给学生）
            if (!force && !string.IsNullOrEmpty(config.PasswordHash))
            {
                bool ok = false;
                if (!string.IsNullOrEmpty(givenPassword))
                {
                    ok = PasswordHasher.Verify(givenPassword, config.PasswordHash);
                    if (!ok) Console.Error.WriteLine("密码不正确，不能卸载。");
                }
                else if (quiet)
                {
                    Console.Error.WriteLine("静默卸载必须提供 --password <密码>，或由老师显式加 --force。");
                    Environment.ExitCode = 3;
                    return;
                }
                else
                {
                    using (var dlg = new PasswordDialog("LabGuard - 卸载", "卸载小助手密码：", config.PasswordHash))
                    {
                        ok = dlg.ShowDialog() == DialogResult.OK;
                    }
                }
                if (!ok)
                {
                    if (!quiet) MessageBox.Show("密码不正确，不能卸载。", "LabGuard");
                    Environment.ExitCode = 3;
                    return;
                }
            }

            if (!quiet && MessageBox.Show(
                    "将卸载【LabGuard】并还原所有被改动的设置：\r\n\r\n" +
                    "· 恢复 USB 存储设备、hosts、浏览器策略、注册表策略、壁纸、IFEO、安全模式限制\r\n" +
                    "· 删除开机启动项、守护服务、快捷方式、控制面板卸载项\r\n\r\n" +
                    "卸载后建议重启电脑。确定继续吗？",
                    "卸载LabGuard", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }

            string dir = GuardEngine.RequireInstallDir();
            Log.MinLevel = LogLevel.Info;
            Log.Info("开始卸载，安装目录 " + dir);

            try { SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName); } catch { }
            try
            {
                foreach (Process p in Process.GetProcessesByName("LabGuard.Agent")) { try { p.Kill(); } catch { } }
            }
            catch { }

            // 1) 还原系统（不启动监控，只跑各模块的还原分支）
            GuardEngine.RestoreEverything(config, dir, false);
            Core.Data.HostsFile.Cleanup();
            if (File.Exists(Core.Data.HostsFile.BackupPath) && !Core.Data.HostsFile.HasManagedBlock())
            {
                try { File.Copy(Core.Data.HostsFile.BackupPath, Core.Data.HostsFile.HostsPath, true); } catch { }
            }
            Core.Data.HostsFile.FlushDns();

            // 2) 清掉自启动、服务、控制面板卸载项
            SystemActions.Run("schtasks.exe", "/delete /tn \"LabGuard\\Agent\" /f");
            SystemActions.Run("sc.exe", "delete " + WatchdogGuard.ServiceName);
            SystemActions.DeleteRegistryKey(Microsoft.Win32.RegistryHive.LocalMachine, WatchdogGuard.ServiceName);
            SystemActions.DeleteRegistryKey(Microsoft.Win32.RegistryHive.LocalMachine, ConfigStore.RegistryRoot);
            SystemActions.DeleteRegistryKey(Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuardReplica");
            // 清掉"安全模式服务清单"里的登记（不留后患）
            foreach (string root in new[]
            {
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\",
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\"
            })
            {
                SystemActions.DeleteRegistryKey(Microsoft.Win32.RegistryHive.LocalMachine, root + WatchdogGuard.ServiceName);
            }

            // 3) 清掉快捷方式
            foreach (string lnk in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "LabGuard.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LabGuard", "设置.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LabGuard", "卸载.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LabGuard", "解除锁定（输入密码）.lnk")
            })
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }
            string programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LabGuard");
            try
            {
                if (Directory.Exists(programsDir) && Directory.GetFileSystemEntries(programsDir).Length == 0)
                    Directory.Delete(programsDir);
            }
            catch { }

            // 4) 清配置（保留日志，便于事后查证）
            try
            {
                // 系统级备份副本（自愈源）
                string mirror = Path.Combine(ConfigStore.DataDir, "payload");
                if (Directory.Exists(mirror)) Directory.Delete(mirror, true);
                foreach (string f in new[]
                {
                    ConfigStore.ConfigPath,
                    Path.Combine(ConfigStore.DataDir, "paused.flag"),
                    Path.Combine(ConfigStore.DataDir, "files.sha256"),
                    Path.Combine(ConfigStore.DataDir, "wallpaper-active.jpg")
                })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            catch { }

            // 5) 删程序目录：自己跑在里面删不掉，交给 %TEMP% 的副本收尾
            if (!keepFiles) StartSelfPurge(dir);

            Log.Info("卸载完成（日志保留在 " + Log.Directory + "）");
            if (!quiet && MessageBox.Show(
                    "卸载完成。\r\n\r\n还原后需要重启电脑才能完全生效（USB、任务栏、安全模式等）。\r\n" +
                    "（日志保留在 " + Log.Directory + "）\r\n\r\n现在重启吗？",
                    "LabGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                SystemActions.Shutdown("/r");
            }
        }

        /// <summary>把自己复制到 %TEMP%，由副本删除整个安装目录（含正在运行的本体）。</summary>
        private static void StartSelfPurge(string dir)
        {
            try
            {
                string temp = Path.Combine(Path.GetTempPath(),
                    "LabGuard-Purge-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Application.ExecutablePath, temp, true);
                var psi = new ProcessStartInfo(temp, "--purge \"" + dir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                Log.Info("已安排删除程序目录：" + dir);
            }
            catch (Exception ex)
            {
                Log.Warn("安排删除程序目录失败：" + ex.Message + "，请手工删除 " + dir);
            }
        }

        /// <summary>等父进程退出后删目录（重试若干次），最后自删临时副本。</summary>
        private static void Purge(string dir)
        {
            var log = new System.Text.StringBuilder();
            string logPath = Path.Combine(Path.GetTempPath(), "labguard-purge.log");
            log.AppendLine(DateTime.Now.ToString("s") + " purge start: " + dir);
            Exception lastError = null;
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    log.AppendLine("第 " + (i + 1) + " 次尝试：删除成功");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    System.Threading.Thread.Sleep(500);
                }
            }
            if (Directory.Exists(dir))
            {
                log.AppendLine("删除失败，最后错误：" + (lastError == null ? "未知" : lastError.GetType().Name + ": " + lastError.Message));
            }
            else
            {
                log.AppendLine("目录已删除");
                // 顺手清理历史上残留的收尾副本
                try
                {
                    foreach (string f in Directory.GetFiles(Path.GetTempPath(), "LabGuard-Purge-*.exe"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                catch { }
            }
            try { File.AppendAllText(logPath, log.ToString(), System.Text.Encoding.UTF8); } catch { }
            try
            {
                string me = Application.ExecutablePath;
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 2 >nul & del /f /q \"" + me + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Value(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
