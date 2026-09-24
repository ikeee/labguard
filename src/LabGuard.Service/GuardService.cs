using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Timers;
using LabGuard.Core;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Logging;

namespace LabGuard.Service
{
    public sealed class GuardService : ServiceBase
    {
        private GuardEngine _engine;
        private Timer _agentWatch;
        private GuardConfig _config;

        public GuardService()
        {
            ServiceName = WatchdogGuard.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            Log.Info("服务启动");
            _config = ConfigStore.Load();
            // 后路：安全模式（含无网络的安全模式）默认不加载管控，留给老师处理/卸载
            if (Core.Interop.SafeModeDetector.ShouldSkipEnforcement(_config.SafeMode.RunInSafeMode))
            {
                Log.Warn("服务处于安全模式：不启用任何策略（老师可在这种模式下彻底卸载/修复）。");
                return;
            }
            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                Log.Warn("尚未设置密码（未安装完成），服务暂不启用管控");
                return;
            }
            if (WatchdogGuard.IsPaused())
            {
                Log.Warn("检测到暂停标记（老师已暂停），服务暂不启用管控");
                return;
            }
            StartEngine();
            EnsureAgent();
            _agentWatch = new Timer(Math.Max(5, _config.Watchdog.AgentRestartSeconds) * 1000) { AutoReset = true };
            _agentWatch.Elapsed += (s, e) => { try { EnsureAgent(); } catch { } };
            _agentWatch.Enabled = true;
        }

        private void StartEngine()
        {
            _engine = new GuardEngine(_config);
            _engine.Start(null, AppDomain.CurrentDomain.BaseDirectory, false, EngineRole.SystemCore);
        }

        private void EnsureAgent()
        {
            if (!_config.Watchdog.Enabled) return;
            if (WatchdogGuard.IsPaused()) return;
            if (Core.Interop.SystemActions.ProcessExists("LabGuard.Agent")) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabGuard.Agent.exe");
            if (!File.Exists(path))
            {
                Log.Warn("未找到代理程序：" + path);
                return;
            }
            try
            {
                // 服务在 SYSTEM 会话里，用计划任务方式把代理拉进当前登录用户会话
                int rc = Core.Interop.SystemActions.Run("schtasks.exe", "/run /tn \"LabGuard\\Agent\" /i");
                if (rc != 0)
                {
                    Core.Interop.SystemActions.Run("cmd.exe", "/c start \"\" \"" + path + "\"");
                }
                Log.Warn("已尝试重新启动小助手代理（schtasks rc=" + rc + "）");
            }
            catch (Exception ex) { Log.Warn("启动代理失败：" + ex.Message); }
        }

        protected override void OnStop()
        {
            Log.Info("服务停止");
            try { _agentWatch?.Stop(); } catch { }
            try { _engine?.Stop(); } catch (Exception ex) { Log.Error("停止引擎失败", ex); }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        // ------------------------------------------------------------------ 控制台调试
        internal void DebugStart() => OnStart(new string[0]);
        internal void DebugStop() => OnStop();
    }
}
