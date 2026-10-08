using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
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
        private System.Timers.Timer _agentWatch;
        private GuardConfig _config;

        // ------------------------------------------------------------ 防拆：秒级复活 + 被杀计数
        private Thread _fastWatch;
        private volatile bool _stopping;
        private readonly List<DateTime> _kills = new List<DateTime>();
        private DateTime _lastRelaunch = DateTime.MinValue;
        private int _relaunchCount;

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
            HardenSelf();
            // 后路：安全模式（含无网络的安全模式）默认不加载管控，留给老师处理/卸载
            if (Core.Interop.SafeModeDetector.ShouldSkipEnforcement(_config.SafeMode.RunInSafeMode))
            {
                Log.Warn("服务处于安全模式：不启用任何策略（老师可在这种模式下彻底卸载/修复）。");
                return;
            }
            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                // 红队 B4c：已安装却没了密码散列 = 配置被清过。按受损处理：管控**继续**（绝不"未配置放行"），
                // 老师修复通道 = 自己介质上的 cleanup-all.ps1 或重装（docs/05）。
                if (Core.Config.ConfigIntegrity.IsSuspiciousEmptyPassword(Core.Config.ConfigStore.IsInstalled(), _config.PasswordHash))
                {
                    Log.Error("密码散列为空但本机标记为已安装——配置疑似被篡改，管控继续启用（修复见 docs/05）");
                    Core.Config.ConfigStore.MarkTamperSuspected();
                }
                else
                {
                    Log.Warn("尚未设置密码（未安装完成），服务暂不启用管控");
                    return;
                }
            }
            // 「暂停」只停**管控策略**，不停**进程守护**：
            // 真机事故（2026-10-08）——老师手势解锁遮罩写入 paused.flag 后，服务既不起引擎也不拉代理，
            // 学生杀掉小助手就再没人管，且托盘没了、老师点不到"启动监控"，只能手工跑设置程序。
            bool paused = WatchdogGuard.IsPaused();
            if (paused)
                Log.Warn("检测到暂停标记（老师已暂停）：不启用管控策略，但进程守护继续（小助手被杀照样拉起）");
            else
                StartEngine();
            EnsureAgent();
            // 兜底轮询（原有逻辑保留）：万一"句柄等待"那条路走不通（权限/会话异常），还有它
            _agentWatch = new System.Timers.Timer(Math.Max(5, _config.Watchdog.AgentRestartSeconds) * 1000) { AutoReset = true };
            _agentWatch.Elapsed += (s, e) => { try { EnsureAgent(); } catch { } };
            _agentWatch.Enabled = true;
            StartFastWatch();
        }

        // ------------------------------------------------------------------ 防拆①：进程加锁
        /// <summary>
        /// 给服务进程套 DACL。服务本来就以 SYSTEM 运行（标准用户动不了），
        /// 这一步主要防止"以登录用户身份读服务内存/注入"，顺带把非管理员的路彻底堵上。
        /// </summary>
        private void HardenSelf()
        {
            if (!(_config.AntiTamper.Enabled && _config.AntiTamper.HardenProcessDacl)) return;
            string detail;
            if (Core.Interop.ProcessHardening.HardenSelf(out detail)) Log.Info("[防拆] 服务进程已加固：" + detail);
            else Log.Warn("[防拆] 服务进程加固失败：" + detail);
        }

        // ------------------------------------------------------------------ 防拆②：秒级复活
        /// <summary>
        /// 拿着小助手的进程句柄等它退出：它一死（被杀/崩溃）立刻重拉，不等 10 秒轮询。
        /// 学生感受到的就是"任务管理器里刚结束，下一秒又冒出来"。
        /// </summary>
        private void StartFastWatch()
        {
            if (!(_config.AntiTamper.Enabled && _config.AntiTamper.FastRestart)) return;
            _stopping = false;
            _fastWatch = new Thread(FastWatchLoop) { IsBackground = true, Name = "LabGuard.AgentFastWatch" };
            _fastWatch.Start();
            Log.Info("[防拆] 已启用秒级复活（句柄等待，不再等轮询间隔）");
        }

        private void FastWatchLoop()
        {
            bool wasAlive = false;      // 只有"确认活过之后又没了"才算被杀，避免把"还没起来/正在拉起"误记成被杀
            while (!_stopping)
            {
                try
                {
                    int pid = FindAgentPid();
                    if (pid <= 0)
                    {
                        if (wasAlive) { wasAlive = false; NoteKilled("已运行的小助手进程消失了"); }
                        EnsureAgent();
                        // 拉起后**等到它真的起来**再继续：否则"刚拉起就被杀"会漏计
                        // （真机演练实测：4 次击杀只记到 1 次，反复被杀的告警永远触发不了）。
                        for (int i = 0; i < 20 && !_stopping; i++)
                        {
                            Thread.Sleep(300);
                            if (FindAgentPid() > 0) { wasAlive = true; break; }
                        }
                        if (!wasAlive) Thread.Sleep(1500);   // 没起来（启动失败/老师暂停）：放慢重试
                        continue;
                    }
                    wasAlive = true;
                    IntPtr h = Core.Interop.ProcessHardening.OpenForWait(pid);
                    if (h == IntPtr.Zero)
                    {
                        Thread.Sleep(1000);     // 拿不到句柄（进程刚结束/权限问题）→ 稍后再试
                        continue;
                    }
                    try
                    {
                        // 切片等待：每 500ms 醒一次，既保证"秒级"，又能及时响应服务停止
                        while (!_stopping && !Core.Interop.ProcessHardening.WaitForExit(h, 500)) { }
                    }
                    finally { Core.Interop.ProcessHardening.Close(h); }

                    if (_stopping) break;
                    wasAlive = false;
                    NoteKilled("小助手进程退出（pid=" + pid + "）");
                    EnsureAgent();
                    for (int i = 0; i < 20 && !_stopping; i++)      // 同上：等它真的起来，再回到句柄等待
                    {
                        Thread.Sleep(300);
                        if (FindAgentPid() > 0) { wasAlive = true; break; }
                    }
                    if (!wasAlive) Thread.Sleep(1500);
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    Log.Warn("[防拆] 快速守护线程异常：" + ex.Message);
                    Thread.Sleep(3000);
                }
            }
        }

        private static int FindAgentPid()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("LabGuard.Agent"))
                {
                    int id = p.Id;
                    p.Dispose();
                    return id;
                }
            }
            catch { }
            return 0;
        }

        // ------------------------------------------------------------------ 防拆⑤：反复被杀告警
        private void NoteKilled(string why)
        {
            DateTime now = DateTime.Now;
            int window = Math.Max(1, _config.AntiTamper.RepeatKillWindowMinutes);
            _kills.RemoveAll(t => (now - t).TotalMinutes > window);
            _kills.Add(now);
            _relaunchCount++;
            _lastRelaunch = now;

            int threshold = Math.Max(2, _config.AntiTamper.RepeatKillCount);
            if (!(_config.AntiTamper.Enabled && _config.AntiTamper.AlertOnRepeatedKill)) return;
            if (_kills.Count < threshold) return;

            string msg = "小助手在 " + window + " 分钟内被结束 " + _kills.Count + " 次（" + why +
                         "）——很可能有人在反复杀进程，已记入防拆标记";
            Log.Warn("[防拆] " + msg);
            try { ConfigStore.KillAlert = true; } catch { }
        }

        /// <summary>最近一次被杀后重新拉起的次数（演练/状态查询用）。</summary>
        public int RelaunchCount { get { return _relaunchCount; } }

        private void StartEngine()
        {
            _engine = new GuardEngine(_config);
            _engine.Start(null, AppDomain.CurrentDomain.BaseDirectory, false, EngineRole.SystemCore);
        }

        private void EnsureAgent()
        {
            // 两个开关任一打开就守护小助手：
            // 「互相守护」是老开关，「进程防拆加固」是新开关 —— 机房常见配置是"只开网络组 + 防拆"，
            // 若这里只看 Watchdog.Enabled，学生一杀进程就没人管了（真机演练实测：20 秒都没回来）。
            // 暂停与否交给纯函数判断（默认**暂停也守护**），别再一句 IsPaused() 就把防拆关掉。
            if (!WatchdogGuard.ShouldKeepAgentAlive(_config, WatchdogGuard.IsPaused())) return;
            if (Core.Interop.SystemActions.ProcessExists("LabGuard.Agent")) return;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabGuard.Agent.exe");
            if (!File.Exists(path))
            {
                // 红队 A6：exe 缺失（运行中被改名/删）时以前只 WARN 不自愈——立即从 payload 补回
                Log.Warn("未找到代理程序：" + path + "，尝试从 payload 恢复……");
                int restored = WatchdogGuard.RestoreFromPayload(AppDomain.CurrentDomain.BaseDirectory, null, null);
                if (restored <= 0 || !File.Exists(path))
                {
                    Log.Error("代理程序缺失且 payload 副本不可用，小助手无法拉起——请重新部署！");
                    return;
                }
            }
            try
            {
                // 服务在 SYSTEM 会话里：只能用"计划任务 + /i"把代理拉进当前登录用户会话。
                // 不要用 cmd start 直接起（会落在会话 0，没有桌面 → 小助手建不了托盘而闪退）。
                int rc = Core.Interop.SystemActions.Run("schtasks.exe", "/run /tn \"LabGuard\\Agent\" /i");
                if (rc != 0)
                {
                    Log.Warn("拉起小助手失败（schtasks rc=" + rc + "）：请确认计划任务 LabGuard\\Agent 存在且已启用。");
                    return;
                }
                Log.Warn("已尝试重新启动小助手代理（schtasks rc=" + rc + "）");
            }
            catch (Exception ex) { Log.Warn("启动代理失败：" + ex.Message); }
        }

        // ------------------------------------------------------------------ 演练：暂停期间杀进程还能不能复活
        /// <summary>
        /// 「老师暂停了监控 → 学生杀掉小助手 → 还会不会被拉起来」的可断言演练。
        /// 这是 2026-10-08 真机事故的直接回归入口（那次是杀掉后 8 小时没人管）。
        /// 步骤：置暂停标记 → 确认小助手在 → 结束它 → 等它回来 → 还原暂停标记。
        /// </summary>
        public static int PauseDrill(int waitSeconds, out string report)
        {
            int budget = Math.Max(5, waitSeconds);
            GuardConfig cfg = ConfigStore.Load();
            if (!cfg.AntiTamper.Enabled)
            {
                report = "演练前提不满足：防拆总开关（AntiTamper.Enabled）是关的";
                return 2;
            }
            if (!WatchdogGuard.ShouldKeepAgentAlive(cfg, true))
            {
                report = "当前配置下暂停期间不守护进程（AntiTamper.KeepAliveWhenPaused=false）：" +
                         "按设计暂停时进程不会被拉起，演练不适用";
                return 2;
            }
            bool wasPaused = WatchdogGuard.IsPaused();
            WatchdogGuard.MarkPaused(true);
            try
            {
                int first = WaitForAgent(budget);
                if (first <= 0)
                {
                    report = "FAIL：置暂停后小助手没起来（守护没生效，或计划任务 LabGuard\\Agent 缺失）";
                    return 1;
                }
                string detail;
                Core.Interop.ProcessHardening.RestoreProcess(first, out detail);   // 先还原 DACL 才杀得掉
                try
                {
                    Process victim = Process.GetProcessById(first);
                    victim.Kill();
                    // Kill() 只是"请求"结束：不 wait 的话进程还会短暂存在，
                    // 下一步 WaitForAgent 会立刻又"看到"它 → 变成假 PASS（pid 都没变）。
                    victim.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    report = "FAIL：结束小助手失败：" + ex.Message;
                    return 1;
                }
                Log.Warn("[防拆演练] 已在暂停状态下结束小助手（pid=" + first + "），等待复活……");
                Thread.Sleep(1000);                     // 给它一点退出的时间，避免拿到正在退出的旧 pid
                int back = WaitForAgent(budget);
                if (back <= 0)
                {
                    report = "FAIL：暂停期间被杀后 " + budget + " 秒内没有复活 —— 防拆仍被暂停挡住了";
                    return 1;
                }
                if (back == first)
                {
                    report = "FAIL：小助手没被真正结束（pid 仍是 " + first + "），演练不算数";
                    return 1;
                }
                report = "PASS：暂停期间小助手被杀后仍会复活（原 pid=" + first + " → 新 pid=" + back +
                         "），防拆已与「监控暂停」解耦";
                return 0;
            }
            finally
            {
                WatchdogGuard.MarkPaused(wasPaused);       // 还原现场，别把老师机器留在暂停态
            }
        }

        /// <summary>
        /// 恢复监控（一次性）：清掉暂停标记，并让**正在运行**的服务/小助手真正切回完整形态。
        /// 只清标记是不够的——服务在 OnStart 时就因暂停跳过了引擎，小助手也是以"已暂停"形态起的，
        /// 两者都不会因为标记消失而自己变回来（老师会以为恢复了、其实还是裸奔）。
        /// </summary>
        public static int ResumeAll(out string report)
        {
            WatchdogGuard.MarkPaused(false);
            Log.Warn("[防拆] 已清除暂停标记（--resume），正在重启守护服务与小助手……");

            int killed = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName("LabGuard.Agent"))
                {
                    int id = p.Id;
                    string detail;
                    Core.Interop.ProcessHardening.RestoreProcess(id, out detail);   // 先还原 DACL 才杀得掉
                    try { p.Kill(); killed++; } catch { }
                    p.Dispose();
                }
            }
            catch { }

            Core.Interop.SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName);
            Thread.Sleep(3000);
            Core.Interop.SystemActions.Run("net.exe", "start " + WatchdogGuard.ServiceName);
            Thread.Sleep(3000);

            bool svcUp = Core.Interop.SystemActions.ProcessExists("LabGuard.Service");
            int pid = WaitForAgent(20);
            report = "已清除暂停标记；结束旧小助手 " + killed + " 个，服务" + (svcUp ? "已重启" : "重启失败（兜底任务会在 1 分钟内拉回）") +
                     "，小助手" + (pid > 0 ? "已重新拉起（pid=" + pid + "）" : "尚未起来") + "。";
            return (svcUp && pid > 0) ? 0 : 1;
        }

        private static int WaitForAgent(int seconds)
        {
            for (int i = 0; i < seconds * 4; i++)
            {
                int pid = FindAgentPid();
                if (pid > 0) return pid;
                Thread.Sleep(250);
            }
            return 0;
        }

        protected override void OnStop()
        {
            Log.Info("服务停止");
            _stopping = true;
            try { if (_fastWatch != null && !_fastWatch.Join(2000)) _fastWatch.Abort(); } catch { }
            try { _agentWatch?.Stop(); } catch { }
            try { _engine?.Stop(); } catch (Exception ex) { Log.Error("停止引擎失败", ex); }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        // ------------------------------------------------------------------ 防拆③：兜底自愈（一次性）
        /// <summary>
        /// 由 SYSTEM 计划任务「LabGuard\Guard」每分钟调用一次：
        /// 服务被删 → 重新注册并启动；服务被停 → 启动；小助手不在 → 拉起。
        /// 这是"学生把服务也干掉"之后的最后一条命：服务和代理同时没了，一分钟内也能回来。
        /// </summary>
        public static int EnsureOnce()
        {
            GuardConfig cfg = ConfigStore.Load();
            if (!cfg.Enabled) return 0;
            // 兜底任务属于"防拆"这条线：只要防拆总开关没关，即便「互相守护」组是关的也要自愈
            if (!cfg.AntiTamper.Enabled) return 0;
            if (!cfg.AntiTamper.ScheduledTaskGuard && !cfg.Watchdog.Enabled) return 0;
            if (Core.Interop.SafeModeDetector.ShouldSkipEnforcement(cfg.SafeMode.RunInSafeMode))
            {
                Log.Info("[防拆] 兜底自愈：处于安全模式，不做任何拉起（给老师留后路）");
                return 0;
            }
            bool paused = WatchdogGuard.IsPaused();
            if (!WatchdogGuard.ShouldKeepAgentAlive(cfg, paused))
            {
                // 限频 + 带恢复办法：以前每分钟一行干巴巴的 INFO，老师既看不见也不知道怎么恢复
                if (paused && WatchdogGuard.PauseHintDue(10))
                    Log.Warn("[防拆] 兜底自愈：老师已暂停，且未开启「暂停时也守护进程」，本次不拉起。" +
                             "要恢复监控：运行 LabGuard.Settings.exe 保存一次，或执行 LabGuard.Service.exe --resume");
                return 0;
            }
            if (Log.DryRun)
            {
                Log.Info("[防拆] 兜底自愈：干跑模式，不做任何拉起");
                return 0;
            }

            int handled = 0;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string svcPath = Path.Combine(dir, "LabGuard.Service.exe");

            bool registered = Core.Interop.SystemActions.ServiceExists(WatchdogGuard.ServiceName);
            if (!registered && File.Exists(svcPath))
            {
                Log.Warn("[防拆] 守护服务被删除，正在重新注册……");
                Core.Interop.SystemActions.Run("sc.exe",
                    "create " + WatchdogGuard.ServiceName + " binPath= \"" + svcPath + "\" start= auto obj= LocalSystem");
                Core.Interop.SystemActions.Run("sc.exe",
                    "description " + WatchdogGuard.ServiceName + " \"LabGuard 机房管控守护服务\"");
                Core.Interop.SystemActions.Run("sc.exe",
                    "failure " + WatchdogGuard.ServiceName + " actions= restart/5000/restart/10000/restart/30000 reset= 300");
                registered = Core.Interop.SystemActions.ServiceExists(WatchdogGuard.ServiceName);
                if (registered) handled++;
            }

            // 红队 A6/B2：关键 exe 在运行中被改名/删除时，以前只打一行 WARN 就完事——
            // 这里立即从 payload 副本恢复，把"发现缺失"和"文件自愈"连成一条路。
            if (!File.Exists(svcPath) || !File.Exists(Path.Combine(dir, "LabGuard.Agent.exe")))
            {
                int restored = WatchdogGuard.RestoreFromPayload(dir, null, null);
                if (restored > 0) { Log.Warn("[防拆] 已从 payload 恢复 " + restored + " 个缺失文件"); handled++; }
                else Log.Error("[防拆] 关键程序文件缺失且 payload 副本不可用，无法自愈——请重新部署！");
            }

            if (registered)
            {
                // 红队 B6a：服务没删、只把启动类型改成 Disabled——以前无人纠正，重启后永久免管控
                string startMode = Core.Interop.SystemActions.QueryWmi(
                    "SELECT StartMode FROM Win32_Service WHERE Name='" + WatchdogGuard.ServiceName + "'", "StartMode");
                if (startMode != null && !startMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("[防拆] 守护服务启动类型被改为 " + startMode + "，已改回自动");
                    Core.Interop.SystemActions.Run("sc.exe", "config " + WatchdogGuard.ServiceName + " start= auto");
                    handled++;
                }
                // 红队 B6b/A8：FailureActions 被清零/改慢没有任何巡检——每轮幂等重写，成本可忽略
                Core.Interop.SystemActions.Run("sc.exe",
                    "failure " + WatchdogGuard.ServiceName + " actions= restart/5000/restart/10000/restart/30000 reset= 300");
            }

            // 红队 D7/B7：两个计划任务是全部复活线的根基。
            // 被删 → 按安装时的参数重建；被禁用 → /change /enable（对已启用任务是幂等 no-op）。
            string agentExe = Path.Combine(dir, "LabGuard.Agent.exe");
            if (Core.Interop.SystemActions.Run("schtasks.exe", "/query /tn \"LabGuard\\Agent\"") != 0)
            {
                Log.Warn("[防拆] 计划任务 LabGuard\\Agent 被删除，正在重建……");
                Core.Interop.SystemActions.Run("schtasks.exe",
                    "/create /tn \"LabGuard\\Agent\" /tr \"\\\"" + agentExe + "\\\"\" /sc onlogon /rl highest /f");
                handled++;
            }
            else
            {
                Core.Interop.SystemActions.Run("schtasks.exe", "/change /tn \"LabGuard\\Agent\" /enable");
            }
            if (Core.Interop.SystemActions.Run("schtasks.exe", "/query /tn \"LabGuard\\Guard\"") != 0)
            {
                Log.Warn("[防拆] 计划任务 LabGuard\\Guard 被删除，正在重建……");
                Core.Interop.SystemActions.Run("schtasks.exe",
                    "/create /tn \"LabGuard\\Guard\" /tr \"'" + svcPath + "' --ensure\" /sc minute /mo 1 /ru SYSTEM /rl highest /f");
                handled++;
            }
            else
            {
                Core.Interop.SystemActions.Run("schtasks.exe", "/change /tn \"LabGuard\\Guard\" /enable");
            }

            if (registered && Core.Interop.SystemActions.ServiceState(WatchdogGuard.ServiceName) != "Running")
            {
                Log.Warn("[防拆] 守护服务未运行（被停止？），正在启动……");
                Core.Interop.SystemActions.StartService(WatchdogGuard.ServiceName);
                handled++;
            }

            if (!Core.Interop.SystemActions.ProcessExists("LabGuard.Agent"))
            {
                Log.Warn("[防拆] 小助手进程不在，正在拉起……");
                int rc = Core.Interop.SystemActions.Run("schtasks.exe", "/run /tn \"LabGuard\\Agent\" /i");
                if (rc == 0) handled++;
                else Log.Warn("[防拆] 拉起小助手失败（schtasks rc=" + rc + "）");
            }

            if (handled > 0) Log.Warn("[防拆] 兜底自愈完成：处理了 " + handled + " 项");
            return handled;
        }

        // ------------------------------------------------------------------ 控制台调试
        internal void DebugStart() => OnStart(new string[0]);
        internal void DebugStop() => OnStop();
    }
}
