using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core
{
    /// <summary>引擎角色：Full = 用户会话（有界面，管进程/窗口/文件）；SystemCore = 服务（SYSTEM 权限，管注册表/服务/hosts）。</summary>
    public enum EngineRole { Full, SystemCore }

    /// <summary>
    /// 策略引擎：按配置装配所有 Guard，统一处理违规动作（提示/结束进程/锁定/关机），
    /// 并支持"密码暂停 / 退出还原"。
    /// </summary>
    public sealed class GuardEngine : IDisposable
    {
        private readonly List<IGuard> _guards = new List<IGuard>();
        private readonly Dictionary<string, DateTime> _recentMessages = new Dictionary<string, DateTime>();
        private GuardContext _context;
        private bool _running;

        public GuardConfig Config { get; private set; }
        public string StatusSnapshot
        {
            get
            {
                if (_context == null) return "未启动";
                lock (_context.StatusList)
                {
                    return string.Join(Environment.NewLine, _context.StatusList);
                }
            }
        }
        public IReadOnlyList<IGuard> Guards => _guards;
        public bool IsRunning => _running;

        public GuardEngine(GuardConfig config)
        {
            Config = config ?? new GuardConfig();
        }

        public void Start(ILockScreen screen, string installDir, bool dryRun, EngineRole role = EngineRole.Full)
        {
            if (_running) return;
            Log.DryRun = dryRun;
            _context = new GuardContext(Config, installDir, screen);
            _context.Violation += OnViolation;

            _guards.Clear();
            if (role == EngineRole.SystemCore)
            {
                // 服务侧：系统级、与用户会话无关的部分
                _guards.Add(new HostsGuard());
                _guards.Add(new UsbGuard());
                _guards.Add(new RegistryAclGuard());
                _guards.Add(new SafeModeGuard());
                _guards.Add(new BrowserPolicyGuard());
                _guards.Add(new FileCreationGuard());
                _guards.Add(new WatchdogGuard());
            }
            else
            {
                // 用户会话侧：需要窗口/前台信息的部分
                _guards.Add(new NetworkGuard());
                _guards.Add(new DnsGuard());
                _guards.Add(new ClassroomGuard());
                _guards.Add(new ProcessBlockGuard());
                _guards.Add(new ShellPolicyGuard());
                _guards.Add(new KeyboardHookGuard());
                _guards.Add(new WallpaperGuard());
                _guards.Add(new WatchdogGuard());
            }

            foreach (IGuard g in _guards) g.Start(_context);
            _running = true;
            Log.Info("策略引擎（" + role + "）已启动：" + _guards.Count + " 个模块" +
                     (dryRun ? "（干跑模式，不修改系统）" : ""));
        }

        /// <summary>密码退出/暂停：停止所有 Guard 并逐项还原系统状态。</summary>
        public void Stop(bool markPaused = false)
        {
            if (!_running) return;
            for (int i = _guards.Count - 1; i >= 0; i--)
            {
                try { _guards[i].Stop(); } catch (Exception ex) { Log.Error("停止 " + _guards[i].Name + " 失败", ex); }
            }
            _running = false;
            if (markPaused) WatchdogGuard.MarkPaused(true);
            Log.Warn("策略引擎已停止并还原系统设置");
        }

        private void OnViolation(object sender, ViolationEventArgs e)
        {
            // 同一条信息 5 秒内不重复弹提示，避免轮询刷屏
            lock (_recentMessages)
            {
                DateTime last;
                if (_recentMessages.TryGetValue(e.Message, out last) && (DateTime.Now - last).TotalSeconds < 5) return;
                _recentMessages[e.Message] = DateTime.Now;
            }

            Log.Warn("[违规][" + e.Guard + "] " + e.Message + (string.IsNullOrEmpty(e.Detail) ? "" : "  (" + e.Detail + ")"));

            switch (e.Action)
            {
                case ViolationAction.Notify:
                    try { _context.Screen?.Notify("LabGuard", e.Message); } catch { }
                    break;
                case ViolationAction.Lock:
                    try { _context.Screen?.Show("LabGuard · 已锁定", e.Message, false); } catch { }
                    break;
                case ViolationAction.Shutdown:
                    try { _context.Screen?.Show("LabGuard", e.Message + "\r\n\r\n电脑将在 10 秒后关机。", false); } catch { }
                    SystemActions.Shutdown("/s");
                    break;
                case ViolationAction.Reboot:
                    try { _context.Screen?.Show("LabGuard", e.Message + "\r\n\r\n电脑将在 10 秒后重启。", false); } catch { }
                    SystemActions.Shutdown("/r");
                    break;
            }
        }

        /// <summary>校验口令（退出/设置/卸载统一入口）。</summary>
        public bool VerifyPassword(string password) => PasswordHasher.Verify(password, Config.PasswordHash);

        /// <summary>
        /// 还原系统：不启动监控，只把所有模块的"还原动作"跑一遍（卸载与"退出程序"用）。
        /// </summary>
        public static void RestoreEverything(GuardConfig config, string installDir, bool dryRun)
        {
            Log.DryRun = dryRun;
            var context = new GuardContext(config, installDir, null);
            var all = new List<IGuard>
            {
                new WatchdogGuard(), new FileCreationGuard(), new BrowserPolicyGuard(), new SafeModeGuard(),
                new RegistryAclGuard(), new UsbGuard(), new HostsGuard(), new WallpaperGuard(),
                new KeyboardHookGuard(), new ShellPolicyGuard(), new ProcessBlockGuard(), new ClassroomGuard(),
                new NetworkGuard()
            };
            foreach (IGuard g in all)
            {
                try
                {
                    // 只走"还原"分支：注入 Context 后直接 Stop（不要先 Start，避免又把系统改一遍）
                    var baseGuard = g as GuardBase;
                    baseGuard?.AttachContext(context);
                    g.Stop();
                }
                catch (Exception ex) { Log.Error("还原 " + g.Name + " 失败", ex); }
            }
            Log.Info("已执行完整还原流程");
        }

        public static string RequireInstallDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir.TrimEnd('\\');
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
        }
    }
}
