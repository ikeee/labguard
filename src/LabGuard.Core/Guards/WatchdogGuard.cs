using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 互相守护（服务与代理互相守护）：
    /// 1) 服务/代理进程被结束 → 自动重新拉起；2) 关键文件被删改 → 校验哈希并告警/锁定；
    /// 3) pause.flag 标记"老师已用密码暂停"，暂停期间不强行拉起，避免互相打架。
    /// </summary>
    public sealed class WatchdogGuard : GuardBase
    {
        public override string Name => "互相守护";
        public override bool Enabled => Context?.Config.Watchdog.Enabled ?? false;
        protected override int IntervalMs => 10000;

        public const string ServiceName = "LabGuardSvc";
        private static string PauseFlag => Path.Combine(Config.ConfigStore.DataDir, "paused.flag");
        private static string ManifestPath => Path.Combine(Config.ConfigStore.DataDir, "files.sha256");

        public static void MarkPaused(bool paused)
        {
            try
            {
                Directory.CreateDirectory(Config.ConfigStore.DataDir);
                if (paused) File.WriteAllText(PauseFlag, DateTime.Now.ToString("o"), new UTF8Encoding(false));
                else if (File.Exists(PauseFlag)) File.Delete(PauseFlag);
            }
            catch { }
        }

        public static bool IsPaused()
        {
            try
            {
                if (!File.Exists(PauseFlag)) return false;
                Config.GuardConfig cfg = Config.ConfigStore.Load();
                if (cfg.ResumeAfterMinutes <= 0) return true;
                DateTime at;
                if (!DateTime.TryParse(File.ReadAllText(PauseFlag), out at)) return true;
                if ((DateTime.Now - at).TotalMinutes >= cfg.ResumeAfterMinutes)
                {
                    File.Delete(PauseFlag);
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        protected override void OnStart()
        {
            WriteManifest();
            // 启动时先做一次"文件体检+自愈"：被删的立刻从系统级备份副本恢复
            if (Context?.Config.Watchdog.RestoreMissingFiles == true) RestoreFromPayload(Context.InstallDir, null, Context);
        }

        protected override void OnTick()
        {
            if (Log.DryRun) { SetStatus("干跑模式：不拉起进程、不校验文件"); return; }
            if (IsPaused()) { SetStatus("老师已暂停监控"); return; }

            bool serviceOk = true;
            if (SystemActions.ServiceExists(ServiceName))
            {
                string state = SystemActions.ServiceState(ServiceName);
                serviceOk = state == "Running";
                if (!serviceOk)
                {
                    Context.Report(Name, "监控服务 " + ServiceName + " 已停止，正在强制启动！", ViolationAction.Notify, state);
                    SystemActions.StartService(ServiceName);
                }
            }

            bool agentOk = SystemActions.ProcessExists("LabGuard.Agent");
            if (!agentOk)
            {
                Context.Report(Name, "小助手进程被结束，正在强制重启！", ViolationAction.Notify);
                Relaunch("LabGuard.Agent.exe");
            }

            List<string> bad = VerifyFiles();
            if (bad.Count > 0)
            {
                string msg = "检测到小助手文件被改动或删除：" + string.Join("、", bad) +
                             "（若为杀毒软件误删，请先卸载杀毒软件后重新安装）";
                Context.Report(Name, msg,
                    Context.Config.Watchdog.LockOnIntegrityFailure ? ViolationAction.Lock : ViolationAction.Notify);
                SetStatus("完整性校验失败（" + bad.Count + " 项）");
                if (Context.Config.Watchdog.RestoreMissingFiles)
                {
                    int restored = RestoreFromPayload(Context.InstallDir, bad, Context);
                    if (restored > 0)
                    {
                        Context.Report(Name, "已从系统级备份副本自动恢复 " + restored + " 个文件。",
                            ViolationAction.Notify, "自愈");
                        return;
                    }
                }
                return;
            }

            SetStatus(serviceOk && agentOk ? "服务与代理正常" : "已尝试恢复被结束的进程");
            SendHeartbeat();
        }

        // ------------------------------------------------------------------ 文件自愈
        /// <summary>系统级备份副本所在目录（安装时由安装程序复制，ACL 只给 SYSTEM/管理员）。</summary>
        public static string PayloadDir => Path.Combine(Config.ConfigStore.DataDir, "payload");

        /// <summary>
        /// 从备份副本恢复被删除/被改动的程序文件。
        /// 正在运行的文件无法直接覆盖 → 先把它改名（Windows 允许改运行中镜像的名字），再从副本写回新的。
        /// </summary>
        public static int RestoreFromPayload(string installDir, List<string> badNames, GuardContext context)
        {
            int restored = 0;
            try
            {
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(PayloadDir)) return 0;
                string[] names =
                {
                    "LabGuard.Agent.exe", "LabGuard.Service.exe", "LabGuard.Settings.exe",
                    "LabGuard.Uninstall.exe", "LabGuard.Launcher.exe", "LabGuard.Core.dll"
                };
                foreach (string name in names)
                {
                    string src = Path.Combine(PayloadDir, name);
                    string dst = Path.Combine(installDir, name);
                    if (!File.Exists(src)) continue;
                    bool missing = !File.Exists(dst);
                    bool changed = !missing && !string.Equals(Sha256(src), Sha256(dst), StringComparison.OrdinalIgnoreCase);
                    if (!missing && !changed) continue;
                    try
                    {
                        if (changed)
                        {
                            string old = dst + ".old";
                            try { if (File.Exists(old)) File.Delete(old); } catch { }
                            try { File.Move(dst, old); } catch { }
                        }
                        File.Copy(src, dst, true);
                        restored++;
                        Log.Warn("已恢复被" + (missing ? "删除" : "修改") + "的文件：" + name);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("恢复文件失败 " + name + "：" + ex.Message);
                    }
                }
                // 壁纸只补缺失的
                string wpPayload = Path.Combine(PayloadDir, "wallpaper");
                string wpTarget = Path.Combine(installDir, "wallpaper");
                if (Directory.Exists(wpPayload))
                {
                    Directory.CreateDirectory(wpTarget);
                    foreach (string f in Directory.GetFiles(wpPayload, "*.jpg"))
                    {
                        string dst = Path.Combine(wpTarget, Path.GetFileName(f));
                        if (File.Exists(dst)) continue;
                        try { File.Copy(f, dst, true); restored++; } catch { }
                    }
                }
                if (restored > 0)
                {
                    try { File.Delete(ManifestPath); } catch { }   // 让下次启动重建校验清单
                }
            }
            catch (Exception ex)
            {
                Log.Warn("文件自愈失败：" + ex.Message);
            }
            return restored;
        }

        // ------------------------------------------------------------------ 心跳上报（可选）
        private DateTime _lastReport = DateTime.MinValue;

        private void SendHeartbeat()
        {
            string url = Context?.Config.Watchdog.ReportUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            if ((DateTime.Now - _lastReport).TotalSeconds < 60) return;
            _lastReport = DateTime.Now;
            bool serviceOk = SystemActions.ServiceExists(ServiceName) && SystemActions.ServiceState(ServiceName) == "Running";
            string body = "{" +
                "\"machine\":\"" + Environment.MachineName + "\"," +
                "\"user\":\"" + Environment.UserName + "\"," +
                "\"time\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"," +
                "\"service\":" + (serviceOk ? "true" : "false") + "," +
                "\"agent\":true," +
                "\"paused\":" + (IsPaused() ? "true" : "false") + "," +
                "\"dns\":\"" + string.Join(",", Context.Config.Network.DnsServers) + "\"," +
                "\"wiredOnly\":" + (Context.Config.Network.WatchWiredOnly ? "true" : "false") + "," +
                "\"tamper\":" + (Config.ConfigStore.TamperSuspected ? "true" : "false") +
                "}";
            string target = url;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(body);
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(target);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Timeout = 5000;
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse()) { }
                    Log.Debug("心跳已上报：" + target);
                }
                catch (Exception ex)
                {
                    Log.Debug("心跳上报失败（不影响管控）：" + ex.Message);
                }
            });
        }

        private void Relaunch(string exeName)
        {
            try
            {
                string path = Path.Combine(Context.InstallDir, exeName);
                if (!File.Exists(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Warn("已重新启动 " + exeName);
            }
            catch (Exception ex) { Log.Warn("重新启动 " + exeName + " 失败：" + ex.Message); }
        }

        private void WriteManifest()
        {
            if (Log.DryRun) return;
            try
            {
                Directory.CreateDirectory(Config.ConfigStore.DataDir);
                var sb = new StringBuilder();
                foreach (string f in MonitoredFiles())
                {
                    if (!File.Exists(f)) continue;
                    sb.AppendLine(Path.GetFileName(f) + "  " + Sha256(f));
                }
                if (sb.Length > 0 && !File.Exists(ManifestPath))
                    File.WriteAllText(ManifestPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private List<string> VerifyFiles()
        {
            var bad = new List<string>();
            try
            {
                if (!File.Exists(ManifestPath)) return bad;
                foreach (string line in File.ReadAllLines(ManifestPath))
                {
                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;
                    string path = Path.Combine(Context.InstallDir, parts[0]);
                    if (!File.Exists(path)) { bad.Add(parts[0] + "(缺失)"); continue; }
                    if (!string.Equals(Sha256(path), parts[1], StringComparison.OrdinalIgnoreCase)) bad.Add(parts[0] + "(被修改)");
                }
            }
            catch { }
            return bad;
        }

        private IEnumerable<string> MonitoredFiles()
        {
            yield return Path.Combine(Context.InstallDir, "LabGuard.Core.dll");
            yield return Path.Combine(Context.InstallDir, "LabGuard.Agent.exe");
            yield return Path.Combine(Context.InstallDir, "LabGuard.Service.exe");
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        protected override void OnStop() { }
    }
}
