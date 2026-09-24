using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;

namespace LabGuard.Installer
{
    /// <summary>
    /// 一键安装的全部动作（不依赖 PowerShell，本程序自身就是管理员）：
    /// 解出安装包体 → 目录权限 → 注册表 → 服务 → 登录计划任务 → 快捷方式 → 卸载登记 → 写入策略 → 启动。
    /// 所有步骤都输出到回调，供向导显示，并同时写入 %ProgramData%\LabGuard\logs\install-*.log。
    /// </summary>
    public sealed class InstallEngine
    {
        private readonly Action<string> _log;
        private readonly List<string> _report = new List<string>();
        public bool DryRun { get; set; }
        public bool NoStart { get; set; }
        public string InstallDir { get; set; }
        public GuardConfig Config { get; set; }
        public string Password { get; set; }

        public const string ServiceName = WatchdogGuard.ServiceName;
        public const string TaskName = @"LabGuard\Agent";

        public InstallEngine(Action<string> log)
        {
            _log = log ?? (s => { });
        }

        private void Say(string line)
        {
            _report.Add(line);
            _log(line);
        }

        private void Run(string exe, string args)
        {
            if (DryRun) { Say("        [预演] " + exe + " " + args); return; }
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    if (!string.IsNullOrWhiteSpace(outp)) Say("        " + outp.Trim());
                    if (!string.IsNullOrWhiteSpace(err)) Say("        " + err.Trim());
                }
            }
            catch (Exception ex)
            {
                Say("        ⚠ 执行失败：" + ex.Message);
            }
        }

        /// <summary>默认安装目录：与原版同款的"内存指纹"路径（学生不容易猜到）。</summary>
        public static string DefaultInstallDir()
        {
            try
            {
                long physMb = 0, virtMb = 0;
                using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object v = mo["TotalPhysicalMemory"];
                        if (v != null) physMb = Convert.ToInt64(v) / 1024 / 1024;
                    }
                }
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVirtualMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object v = mo["TotalVirtualMemorySize"];
                        if (v != null) virtMb = Convert.ToInt64(v) / 1024;
                    }
                }
                if (physMb > 0 && virtMb > 0) return @"C:\f" + physMb + virtMb;
            }
            catch { }
            return @"C:\LabGuard";
        }

        public void Install()
        {
            Say("=== 开始安装 ===");
            Say("安装目录：" + InstallDir);

            if (!DryRun) Directory.CreateDirectory(InstallDir);

            Say("1) 释放程序文件");
            ExtractPayload(InstallDir);
            Say("   已释放 " + CountPayload() + " 个文件");

            Say("1.5) 建立系统级备份副本（用于「被删就自动恢复」，普通用户无权访问）");
            string mirror = Path.Combine(ConfigStore.DataDir, "payload");
            if (!DryRun)
            {
                Directory.CreateDirectory(mirror);
                ExtractPayload(mirror);
                ApplyAcl(mirror, true);
            }
            Say("   备份副本：" + mirror);
            if (!DryRun) ExtractToolsTo(mirror);

            Say("2) 加固安装目录权限（管理员/SYSTEM 完全控制，普通用户只读）");
            ApplyAcl(InstallDir, false);

            Say("3) 写注册表（HKLM\\SOFTWARE\\LabGuard）");
            if (!DryRun)
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(ConfigStore.RegistryRoot))
                {
                    if (k != null)
                    {
                        k.SetValue("Installed", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        k.SetValue("InstallDir", InstallDir, Microsoft.Win32.RegistryValueKind.String);
                        k.SetValue("Version", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                }
            }

            // 服务镜像放"备份副本"里：学生删掉安装目录也删不掉正在运行的服务，服务随后会把安装目录自动恢复回来
            string svcExe = Path.Combine(mirror, "LabGuard.Service.exe");
            string agentExe = Path.Combine(InstallDir, "LabGuard.Agent.exe");

            Say("4) 安装守护服务 " + ServiceName);
            StopAndDeleteService();
            Run("sc.exe", "create " + ServiceName + " binPath= \"" + svcExe + "\" start= auto obj= LocalSystem DisplayName= \"机房管理助手守护服务（复刻）\"");
            Run("sc.exe", "description " + ServiceName + " \"机房管理助手（复刻）：系统级策略守护，保证小助手与电子教室保护持续生效。\"");
            Run("sc.exe", "failure " + ServiceName + " actions= restart/60000/restart/60000/restart/60000 reset= 900");
            Say("4.5) 加固服务：普通用户不能停止，只能查询/启动；安全模式不加载（留给老师处理）");
            Run("sc.exe", "sdset " + ServiceName + " \"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCSWRPRC;;;BU)\"");
            Run("sc.exe", "failureflag " + ServiceName + " 1");
            Run("sc.exe", "failure " + ServiceName + " actions= restart/5000/restart/5000/restart/5000 reset= 300");
            RemoveSafeModeServiceRegistration();

            Say("5) 创建登录自启计划任务（最高权限，不需要改 UAC）");
            Run("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f");
            Run("schtasks.exe", "/create /tn \"" + TaskName + "\" /tr \"" + agentExe + "\" /sc onlogon /rl highest /f");

            Say("6) 写入策略与密码");
            if (!DryRun)
            {
                if (!string.IsNullOrEmpty(Password)) Config.PasswordHash = PasswordHasher.Create(Password);
                ConfigStore.Save(Config);
                // NoStart（装好先不启用）或总开关关闭时，写"暂停标记"：
                // 小助手仍会随登录启动、托盘可用，但不会下发任何策略；在设置里保存一次即可恢复。
                WatchdogGuard.MarkPaused(NoStart || !Config.Enabled);
            }

            Say("7) 创建快捷方式");
            CreateShortcuts(agentExe);

            Say("8) 登记卸载项（控制面板可见）");
            if (!DryRun)
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuardReplica"))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", "机房管理助手（复刻）");
                        k.SetValue("UninstallString", "\"" + Path.Combine(InstallDir, "LabGuard.Uninstall.exe") + "\"");
                        k.SetValue("InstallLocation", InstallDir);
                        k.SetValue("Publisher", "LabGuard Replica");
                        k.SetValue("DisplayVersion", "1.0.0");
                    }
                }
            }

            Say("9) 启动守护服务与登录自启");
            if (NoStart)
            {
                Say("   已按选择跳过启动（策略先不生效，随时可在设置里启用）");
            }
            else
            {
                Run("sc.exe", "start " + ServiceName);
                Run("schtasks.exe", "/run /tn \"" + TaskName + "\" /i");
            }

            Say("=== 安装完成 ===");
            FlushLog();
        }

        // ------------------------------------------------------------------ 明细
        private static IEnumerable<string> PayloadNames()
        {
            string prefix = "payload/";
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                string normalized = n.Replace('\\', '/');
                int idx = normalized.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                yield return normalized.Substring(idx + prefix.Length);
            }
        }

        private static int CountPayload()
        {
            int n = 0;
            foreach (string _ in PayloadNames()) n++;
            return n;
        }

        private void ExtractPayload(string dir)
        {
            if (DryRun)
            {
                foreach (string rel in PayloadNames()) Say("        [预演] 释放 " + rel);
                return;
            }
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
            {
                string normalized = name.Replace('\\', '/');
                int idx = normalized.IndexOf("payload/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string rel = normalized.Substring(idx + "payload/".Length);
                string target = Path.Combine(dir, rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream s = asm.GetManifestResourceStream(name))
                using (FileStream fs = File.Create(target))
                {
                    s.CopyTo(fs);
                }
            }
        }

        /// <summary>把内嵌的救援脚本写进"系统级备份副本\tools"（只有 SYSTEM/管理员能访问）。</summary>
        private void ExtractToolsTo(string dir)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string toolsDir = Path.Combine(dir, "tools");
                foreach (string res in asm.GetManifestResourceNames())
                {
                    string normalized = res.Replace('\\', '/');
                    if (normalized.IndexOf("tools/", StringComparison.OrdinalIgnoreCase) != 0) continue;
                    string name = normalized.Substring("tools/".Length);
                    Directory.CreateDirectory(toolsDir);
                    using (Stream s = asm.GetManifestResourceStream(res))
                    using (FileStream fs = File.Create(Path.Combine(toolsDir, name)))
                    {
                        s.CopyTo(fs);
                    }
                    Say("       救援脚本已放入：" + Path.Combine(toolsDir, name));
                }
            }
            catch (Exception ex) { Say("        ⚠ 写入救援脚本失败：" + ex.Message); }
        }

        /// <summary>收紧目录权限：systemOnly=true 时连 Users 的读取也不给（用于备份副本）。</summary>
        private void ApplyAcl(string dir, bool systemOnly)
        {
            if (DryRun) { Say("        [预演] 设置目录 ACL"); return; }
            try
            {
                var info = new DirectoryInfo(dir);
                DirectorySecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                if (!systemOnly)
                {
                    sec.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                        FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                }
                info.SetAccessControl(sec);
            }
            catch (Exception ex) { Say("        ⚠ 设置权限失败：" + ex.Message); }
        }

        /// <summary>
        /// 确保本服务**不在**"安全模式服务清单"里：安全模式（尤其无网络的安全模式）是老师的后路——
        /// 进安全模式时服务/小助手不加载，老师可以彻底修复、重置或卸载。
        /// （同时清理历史版本可能留下的登记。）
        /// </summary>
        private void RemoveSafeModeServiceRegistration()
        {
            if (DryRun) { Say("        [预演] 确认安全模式不加载本服务（并清理历史登记）"); return; }
            foreach (string root in new[]
            {
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal",
                @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network"
            })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root, true))
                    {
                        k?.DeleteSubKey(ServiceName, false);
                    }
                }
                catch { }
            }
        }

        private void StopAndDeleteService()
        {
            if (DryRun) { Say("        [预演] 停止并删除已有服务（如有）"); return; }
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
                {
                    if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch { }
            Process[] agents = Process.GetProcessesByName("LabGuard.Agent");
            foreach (Process p in agents) { try { p.Kill(); } catch { } }
            Run("sc.exe", "delete " + ServiceName);
        }

        private void CreateShortcuts(string agentExe)
        {
            if (DryRun) { Say("        [预演] 桌面：学生机房管理助手 / 开始菜单：设置、卸载、解除锁定"); return; }
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "机房管理助手");
                Directory.CreateDirectory(programs);

                Action<string, string, string> make = (path, target, args) =>
                {
                    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
                    if (!string.IsNullOrEmpty(args)) lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                };

                make(Path.Combine(desktop, "学生机房管理助手.lnk"), agentExe, null);
                make(Path.Combine(programs, "设置.lnk"), Path.Combine(InstallDir, "LabGuard.Settings.exe"), null);
                make(Path.Combine(programs, "卸载.lnk"), Path.Combine(InstallDir, "LabGuard.Uninstall.exe"), null);
                make(Path.Combine(programs, "解除锁定（输入密码）.lnk"), agentExe, "--unlock");
            }
            catch (Exception ex) { Say("        ⚠ 创建快捷方式失败：" + ex.Message); }
        }

        private void FlushLog()
        {
            try
            {
                string dir = Path.Combine(ConfigStore.DataDir, "logs");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"),
                    _report, new UTF8Encoding(false));
                Say("安装日志：" + dir);
            }
            catch { }
        }
    }
}
