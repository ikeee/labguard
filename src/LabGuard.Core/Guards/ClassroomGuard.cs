using System;
using System.Diagnostics;
using System.IO;
using LabGuard.Core.Data;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 电子教室保护（核心模块）：
    /// 1) 进程被挂起 → 强制恢复；2) 进程被结束 → 重新拉起；
    /// 3) 关键服务被停止 → 重新启动；4) 极域频道/自动登录参数被改 → 还原；5) 找不到进程 → 报告。
    /// </summary>
    public sealed class ClassroomGuard : GuardBase
    {
        public override string Name => "电子教室保护";
        public override bool Enabled => Context?.Config.Classroom.Enabled ?? false;
        protected override int IntervalMs => 5000;

        private string _runningProcessName;
        private int _killedCount;

        protected override void OnStart()
        {
            _runningProcessName = FindClassroomProcess();
            if (_runningProcessName == null)
            {
                Log.Warn("未找到电子教室客户端进程（可在设置中手动指定路径）");
            }
        }

        protected override void OnTick()
        {
            var c = Context.Config.Classroom;
            string procName = _runningProcessName ?? FindClassroomProcess();

            if (procName == null)
            {
                SetStatus("未找到电子教室进程");
                return;
            }
            _runningProcessName = procName;

            Process[] procs = SystemActions.GetProcessesSafe(Path.GetFileNameWithoutExtension(procName));
            if (procs.Length == 0)
            {
                _killedCount++;
                SetStatus("进程未运行（第 " + _killedCount + " 次检测到）");
                Context.Report(Name, "未找到课堂软件进程（或本机网络地址异常）。", HostAction(),
                    "进程：" + procName);
                if (c.RelaunchWhenKilled) Relaunch();
                return;
            }

            foreach (Process p in procs)
            {
                if (c.ResumeWhenSuspended && SystemActions.IsProcessSuspended(p))
                {
                    SetStatus("检测到进程被挂起，已强制恢复");
                    Context.Report(Name, "课堂软件的进程被挂起，已自动恢复。", ViolationAction.Notify, procName);
                    SystemActions.ResumeProcess(p);
                }
                p.Dispose();
            }

            if (c.RequiredServices != null && c.RequiredServices.Count > 0)
            {
                foreach (string svc in c.RequiredServices)
                {
                    if (!SystemActions.ServiceExists(svc)) continue;
                    string state = SystemActions.ServiceState(svc);
                    if (state != "Running")
                    {
                        SetStatus("关键服务 " + svc + " 未运行，已重启");
                        Context.Report(Name, "电子教室的重要服务停止！已强制启动：" + svc, ViolationAction.Notify, state);
                        SystemActions.StartService(svc);
                    }
                }
            }

            if (c.GuardELearningParameters) GuardELearningParameters();
            if (Status != "运行中" && Status != "检测到进程被挂起，已强制恢复") SetStatus("监控中");
        }

        private const string ElearningKey = @"SOFTWARE\TopDomain\e-Learning Class\Student";
        private const string ElearningKey32 = @"SOFTWARE\Wow6432Node\TopDomain\e-Learning Class\Student";

        private void GuardELearningParameters()
        {
            foreach (string key in new[] { ElearningKey, ElearningKey32 })
            {
                object channel = SystemActions.GetRegistryValue(RegistryHive.LocalMachine, key, "ChannelId", RegistryView.Registry64);
                if (channel == null) continue;
                // 只做"存在性/合法性"自愈：把非数字或越界的频道号拉回 1
                int ch;
                if (!int.TryParse(channel.ToString(), out ch) || ch <= 0)
                {
                    Context.Report(Name, "课堂软件的频道/登录参数被修改，已自动还原。", ViolationAction.Notify, key);
                    SystemActions.SetRegistryValue(RegistryHive.LocalMachine, key, "ChannelId", 1, RegistryValueKind.DWord);
                }
            }
        }

        private string FindClassroomProcess()
        {
            var c = Context.Config.Classroom;
            if (!string.IsNullOrWhiteSpace(c.MainExecutable) && File.Exists(c.MainExecutable))
            {
                return Path.GetFileName(c.MainExecutable);
            }
            foreach (string name in c.ProcessNames)
            {
                if (SystemActions.ProcessExists(name)) return name;
            }
            foreach (var hint in DefaultBlocklists.ClassroomHints)
            {
                if (File.Exists(hint.Value)) return hint.Key;
            }
            return null;
        }

        private ViolationAction HostAction()
        {
            var n = Context.Config.Network;
            if (n.ShutdownOnViolation) return ViolationAction.Shutdown;
            if (n.LockOnViolation) return ViolationAction.Lock;
            return ViolationAction.Notify;
        }

        private void Relaunch()
        {
            var c = Context.Config.Classroom;
            string path = !string.IsNullOrWhiteSpace(c.MainExecutable) && File.Exists(c.MainExecutable)
                ? c.MainExecutable
                : null;
            if (path == null)
            {
                foreach (var hint in DefaultBlocklists.ClassroomHints)
                {
                    if (File.Exists(hint.Value)) { path = hint.Value; break; }
                }
            }
            if (path == null) return;

            if (Log.DryRun)
            {
                Log.Warn("[dry] 将重新启动电子教室客户端 " + path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Warn("已重新启动电子教室客户端 " + path);
            }
            catch (Exception ex) { Log.Warn("重启电子教室客户端失败：" + ex.Message); }
        }

        protected override void OnStop() { }
    }
}
