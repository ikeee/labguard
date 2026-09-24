using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 所有"会改动系统"的操作统一走这里：DryRun 时只记录日志不真做，
    /// 这样可以在非机房机器上安全地验证整套逻辑。
    /// </summary>
    public static class SystemActions
    {
        private static readonly List<string> Audit = new List<string>();
        private static readonly object AuditGate = new object();
        public static IReadOnlyList<string> AuditTrail { get { lock (AuditGate) return Audit.ToArray(); } }

        private static void Trace(string entry)
        {
            lock (AuditGate)
            {
                Audit.Add(entry);
                if (Audit.Count > 5000) Audit.RemoveRange(0, 1000);
            }
        }

        private static bool Dry => Log.DryRun;

        // ------------------------------------------------------------------ 注册表
        public static void SetRegistryValue(RegistryHive hive, string subKey, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            string full = Hive(hive) + "\\" + subKey + "\\" + name + " = " + value;
            Trace("SET-REG " + full);
            if (Dry) { Log.Debug("[dry] SET-REG " + full); return; }
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).CreateSubKey(subKey, true))
                {
                    k?.SetValue(name, value, kind);
                }
                Log.Debug("SET-REG " + full);
            }
            catch (Exception ex) { Log.Warn("写注册表失败 " + full + " :: " + ex.Message); }
        }

        public static void DeleteRegistryValue(RegistryHive hive, string subKey, string name)
        {
            Trace("DEL-REG " + Hive(hive) + "\\" + subKey + "\\" + name);
            if (Dry) { Log.Debug("[dry] DEL-REG " + Hive(hive) + "\\" + subKey + "\\" + name); return; }
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey, true))
                {
                    k?.DeleteValue(name, false);
                }
                Log.Debug("DEL-REG " + Hive(hive) + "\\" + subKey + "\\" + name);
            }
            catch (Exception ex) { Log.Warn("删注册表值失败 " + subKey + "\\" + name + " :: " + ex.Message); }
        }

        public static void DeleteRegistryKey(RegistryHive hive, string subKey)
        {
            Trace("DEL-KEY " + Hive(hive) + "\\" + subKey);
            if (Dry) { Log.Debug("[dry] DEL-KEY " + Hive(hive) + "\\" + subKey); return; }
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
                {
                    k.DeleteSubKeyTree(subKey, false);
                }
                Log.Info("删除注册表键 " + Hive(hive) + "\\" + subKey);
            }
            catch (Exception ex) { Log.Warn("删注册表键失败 " + subKey + " :: " + ex.Message); }
        }

        public static object GetRegistryValue(RegistryHive hive, string subKey, string name, RegistryView view = RegistryView.Default)
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKey))
                {
                    return k?.GetValue(name);
                }
            }
            catch { return null; }
        }

        public static bool RegistryKeyExists(RegistryHive hive, string subKey)
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey))
                {
                    return k != null;
                }
            }
            catch { return false; }
        }

        private static string Hive(RegistryHive h) => h == RegistryHive.LocalMachine ? "HKLM" : h == RegistryHive.CurrentUser ? "HKCU" : h.ToString();

        // ------------------------------------------------------------------ 命令行
        public static int Run(string fileName, string arguments, int timeoutMs = 20000)
        {
            Trace("RUN " + fileName + " " + arguments);
            if (Dry) { Log.Debug("[dry] RUN " + fileName + " " + arguments); return 0; }
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(timeoutMs);
                    Log.Debug("RUN " + fileName + " " + arguments + " -> exit " + (p.HasExited ? p.ExitCode : -1));
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("执行命令失败 " + fileName + " " + arguments + " :: " + ex.Message);
                return -1;
            }
        }

        public static void Shutdown(string kind)
        {
            // kind: /s 关机 /r 重启 /l 注销
            Trace("SHUTDOWN " + kind);
            if (Dry) { Log.Warn("[dry] SHUTDOWN " + kind); return; }
            Log.Warn("执行关机指令 " + kind);
            try
            {
                bool old = false;
                NativeMethods.RtlAdjustPrivilege(19, true, false, ref old); // SeShutdownPrivilege
            }
            catch { }
            Run("shutdown.exe", kind + " /f /t 0");
        }

        public static void LockWorkstation()
        {
            Trace("LOCK-WORKSTATION");
            if (Dry) { Log.Warn("[dry] LOCK-WORKSTATION"); return; }
            try { NativeMethods.LockWorkStation(); } catch { }
        }

        // ------------------------------------------------------------------ 服务
        public static bool ServiceExists(string name)
        {
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    bool match = string.Equals(sc.ServiceName, name, StringComparison.OrdinalIgnoreCase);
                    sc.Dispose();
                    if (match) return true;
                }
            }
            catch { }
            return false;
        }

        public static string ServiceState(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    return sc.Status.ToString();
                }
            }
            catch { return "NotInstalled"; }
        }

        public static void StartService(string name)
        {
            Trace("START-SERVICE " + name);
            if (Dry) { Log.Debug("[dry] START-SERVICE " + name); return; }
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                }
            }
            catch (Exception ex) { Log.Warn("启动服务失败 " + name + " :: " + ex.Message); }
        }

        // ------------------------------------------------------------------ 进程
        public static bool ProcessExists(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;
            string bare = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4) : processName;
            try { return Process.GetProcessesByName(bare).Length > 0; }
            catch { return false; }
        }

        public static Process[] GetProcessesSafe(string name)
        {
            try { return Process.GetProcessesByName(name); }
            catch { return new Process[0]; }
        }

        public static bool KillProcess(Process p)
        {
            if (p == null) return false;
            Trace("KILL " + SafeName(p));
            if (Dry) { Log.Warn("[dry] KILL " + SafeName(p)); return true; }
            try { p.Kill(); Log.Warn("已结束进程 " + SafeName(p)); return true; }
            catch (Exception ex) { Log.Warn("结束进程失败 " + SafeName(p) + " :: " + ex.Message); return false; }
        }

        public static void ResumeProcess(Process p)
        {
            if (p == null) return;
            Trace("RESUME " + SafeName(p));
            if (Dry) { Log.Warn("[dry] RESUME " + SafeName(p)); return; }
            try
            {
                NativeMethods.NtResumeProcess(p.Handle);
                Log.Warn("已恢复被挂起的进程 " + SafeName(p));
            }
            catch (Exception ex) { Log.Warn("恢复进程失败 :: " + ex.Message); }
        }

        /// <summary>判断进程是否处于挂起状态（所有线程都挂起）。</summary>
        public static bool IsProcessSuspended(Process p)
        {
            try
            {
                foreach (ProcessThread t in p.Threads)
                {
                    if (t.ThreadState == System.Diagnostics.ThreadState.Wait &&
                        t.WaitReason == ThreadWaitReason.Suspended) return true;
                }
            }
            catch { }
            return false;
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName + "(" + p.Id + ")"; } catch { return "process"; }
        }

        // ------------------------------------------------------------------ WMI
        public static string QueryWmi(string wql, string property, string scope = @"root\cimv2")
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, wql))
                using (ManagementObjectCollection col = searcher.Get())
                {
                    foreach (ManagementObject mo in col)
                    {
                        object v = mo[property];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch (Exception ex) { Log.Debug("WMI 查询失败 " + wql + " :: " + ex.Message); }
            return null;
        }

        // ------------------------------------------------------------------ 文件
        public static void SetFileAttributeHidden(string path, bool hidden)
        {
            Trace("ATTR " + path + " hidden=" + hidden);
            if (Dry) { Log.Debug("[dry] ATTR " + path + " hidden=" + hidden); return; }
            try
            {
                if (!File.Exists(path)) return;
                FileAttributes attr = File.GetAttributes(path);
                File.SetAttributes(path, hidden ? (attr | FileAttributes.Hidden | FileAttributes.System) : (attr & ~(FileAttributes.Hidden | FileAttributes.System)));
            }
            catch (Exception ex) { Log.Warn("设置文件属性失败 " + path + " :: " + ex.Message); }
        }

        public static bool DeleteFile(string path)
        {
            Trace("DELETE " + path);
            if (Dry) { Log.Warn("[dry] DELETE " + path); return true; }
            try
            {
                if (!File.Exists(path)) return false;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                Log.Warn("已删除违规文件 " + path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("删除文件失败 " + path + " :: " + ex.Message);
                return false;
            }
        }

        public static void CopyFile(string src, string dst, bool overwrite = true)
        {
            Trace("COPY " + src + " -> " + dst);
            if (Dry) { Log.Debug("[dry] COPY " + src + " -> " + dst); return; }
            try
            {
                string dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(src, dst, overwrite);
            }
            catch (Exception ex) { Log.Warn("复制文件失败 " + src + " -> " + dst + " :: " + ex.Message); }
        }
    }

}
