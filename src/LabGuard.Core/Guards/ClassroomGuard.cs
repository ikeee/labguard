using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LabGuard.Core.Data;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 电子教室保护（核心模块）：
    /// 1) 关键客户端被结束 → 重新拉起；
    /// 2) 被保护的进程被挂起 → 强制恢复（**按需进程也算**：挂起一定可疑，缺失则不算）；
    /// 3) 关键服务被停止 → 重新启动；
    /// 4) 极域频道参数 / 噢易云桌面服务器地址被改 → 还原；
    /// 5) 找不到关键客户端 → 报告。
    ///
    /// 多产品支持：极域 / 红蜘蛛 / 锐捷 / **噢易（Os-Easy）**。
    /// 噢易教学系统一装就是好几个进程（Student.exe 常驻、MultiClient/Ctsc_Multi 按需起），
    /// 所以这里分成两档：
    ///   · **关键客户端**（MainExecutable 或自动识别到的那一个）= 必须常驻：不在就报告并拉起；
    ///   · **被保护进程**（ProcessNames 里其它名字）= 存在就盯着（防挂起），不存在不吭声，
    ///     否则那些"广播时才启动"的进程会每次轮询都误报一次。
    /// </summary>
    public sealed class ClassroomGuard : GuardBase
    {
        public override string Name => "电子教室保护";
        public override bool Enabled => Context?.Config.Classroom.Enabled ?? false;
        protected override int IntervalMs => 5000;

        private string _runningProcessName;
        private int _killedCount;
        private int _consecutiveMissing;
        private DateTime _lastRelaunchAttempt = DateTime.MinValue;

        /// <summary>两次拉起尝试之间的最小间隔：拉起的进程可能要几秒才落地，连着 Start 只会制造空转噪音（红队 A5）。</summary>
        private static readonly TimeSpan RelaunchThrottle = TimeSpan.FromSeconds(15);
        /// <summary>连续缺失多少轮（≈30 秒）升级为 ERROR：告诉老师"不是闪退，是拉不回来了"。</summary>
        private const int MissingEscalateRounds = 6;

        protected override void OnStart()
        {
            var c = Context.Config.Classroom;
            _runningProcessName = FindClassroomProcess();
            if (_runningProcessName == null)
            {
                Log.Warn("未找到电子教室客户端进程（可在设置中手动指定路径）");
            }
            // 把"这一轮到底在守什么"写进日志：排查现场时（学生说"没管住"）第一眼要看的就是这行
            Log.Info("电子教室保护：关键客户端 = " + (_runningProcessName ?? "（未识别，需在设置里手填）")
                + "；被保护进程 = " + string.Join(" / ", c.ProcessNames)
                + "；关键服务 = " + (c.RequiredServices == null || c.RequiredServices.Count == 0
                    ? "（不检查）" : string.Join(" / ", c.RequiredServices)));
        }

        protected override void OnTick()
        {
            var c = Context.Config.Classroom;

            // ---- 1) 被保护的进程：挂起就恢复（含按需进程；不因缺失而报告）----
            int watched = 0;
            foreach (string name in c.ProcessNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string bare = Path.GetFileNameWithoutExtension(name);
                Process[] ps = SystemActions.GetProcessesSafe(bare);
                watched += ps.Length;
                foreach (Process p in ps)
                {
                    if (c.ResumeWhenSuspended && SystemActions.IsProcessSuspended(p))
                    {
                        SetStatus("检测到 " + bare + " 被挂起，已强制恢复");
                        Context.Report(Name, "课堂软件的进程被挂起，已自动恢复：" + bare + "（或用「结束进程」对抗管控）",
                            ViolationAction.Notify, name);
                        SystemActions.ResumeProcess(p);
                    }
                    p.Dispose();
                }
            }

            // ---- 2) 关键客户端：必须常驻，不在就报告并按需拉起 ----
            string procName = _runningProcessName ?? FindClassroomProcess();
            if (procName == null)
            {
                SetStatus("未找到电子教室进程");
                if (watched > 0) Log.Debug("已按进程名监控到 " + watched + " 个课堂软件进程（未识别出关键客户端）");
                return;
            }
            _runningProcessName = procName;

            Process[] procs = SystemActions.GetProcessesSafe(Path.GetFileNameWithoutExtension(procName));
            if (procs.Length == 0)
            {
                _killedCount++;
                _consecutiveMissing++;
                SetStatus("关键客户端未运行（第 " + _killedCount + " 次检测到）");
                Context.Report(Name, "未找到课堂软件进程（或本机网络地址异常）。", HostAction(),
                    "进程：" + procName);
                if (_consecutiveMissing == MissingEscalateRounds)
                {
                    // 检测 ≠ 恢复（红队 A5）：30 秒还没拉回来，必须让老师看得出"没救回来"
                    Log.Error("关键客户端 " + procName + " 已连续约 " + (MissingEscalateRounds * IntervalMs / 1000)
                        + " 秒未能拉回——请现场检查（第三方自身守护可能也失效，如噢易 MMPC 的 Get Wrong Token）");
                }
                if (c.RelaunchWhenKilled) Relaunch(procName);
            }
            else
            {
                _consecutiveMissing = 0;
                foreach (Process p in procs)
                {
                    if (c.ResumeWhenSuspended && SystemActions.IsProcessSuspended(p))
                    {
                        SetStatus("关键客户端被挂起，已强制恢复");
                        Context.Report(Name, "课堂软件的进程被挂起，已自动恢复。", ViolationAction.Notify, procName);
                        SystemActions.ResumeProcess(p);
                    }
                    p.Dispose();
                }
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
            if (c.GuardVoiParameters) GuardVoiParameters();

            // Debug 级：默认不输出（Log.MinLevel=Info），排查现场时用 --verbose 能看到每轮到底看到了什么
            Log.Debug("巡检：关键客户端 " + procName + " 命中 " + procs.Length + " 个；被保护进程共命中 " + watched + " 个");

            if (Status != "运行中"
                && Status != "检测到进程被挂起，已强制恢复"
                && !Status.StartsWith("检测到 ", StringComparison.Ordinal)
                && Status != "关键客户端被挂起，已强制恢复") SetStatus("监控中");
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

        private const string VoiClientKey = @"SOFTWARE\VoiClient\client";
        private const string VoiServerValue = "gtserver";

        /// <summary>
        /// 噢易 VOI 云桌面：守护"云桌面服务器地址"（gtserver）。
        /// 学生把这个地址改掉/清空，就能让云桌面连不上服务器（本机不还原、变成"自己的系统"）。
        /// 与极域那段一样保持克制：
        ///   · 配置里填了期望值 → 不一致就写回期望值；
        ///   · 没填期望值 → 只在明显非法（空 / 不是 IPv4 也不是主机名）时**报告**，不猜着写。
        /// </summary>
        private void GuardVoiParameters()
        {
            if (!SystemActions.RegistryKeyExists(RegistryHive.LocalMachine, VoiClientKey)) return;
            object raw = SystemActions.GetRegistryValue(RegistryHive.LocalMachine, VoiClientKey, VoiServerValue, RegistryView.Registry64);
            if (raw == null) return;
            string actual = Convert.ToString(raw);

            string expected = Context.Config.Classroom.VoiGtServer;
            if (VoiServerNeedsFix(actual, expected))
            {
                if (!string.IsNullOrWhiteSpace(expected))
                {
                    Context.Report(Name, "云桌面服务器地址被修改，已自动还原。", ViolationAction.Notify,
                        VoiServerValue + "：" + actual + " -> " + expected);
                    SystemActions.SetRegistryValue(RegistryHive.LocalMachine, VoiClientKey, VoiServerValue, expected, RegistryValueKind.String);
                }
                else
                {
                    Context.Report(Name, "云桌面服务器地址异常（非法的 IP/主机名），请检查是否被篡改。", ViolationAction.Notify,
                        VoiServerValue + " = " + actual);
                }
            }
        }

        /// <summary>
        /// VOI 服务器地址是否需要处置（纯判定，便于自检）。
        /// expected 为空时只判"合法性"；填了 expected 就要求严格一致。
        /// </summary>
        public static bool VoiServerNeedsFix(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual)) return true;
            string a = actual.Trim();
            if (!string.IsNullOrWhiteSpace(expected))
            {
                return !string.Equals(a, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            return !LooksLikeHostOrIp(a);
        }

        /// <summary>合法 = IPv4 地址或普通主机名（字母/数字/点/横线）。</summary>
        public static bool LooksLikeHostOrIp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            string[] parts = t.Split('.');
            bool allNumeric = parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
            if (allNumeric)
            {
                return parts.All(p =>
                {
                    int v;
                    return int.TryParse(p, out v) && v >= 0 && v <= 255;
                });
            }
            return t.Length <= 253 && t.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-');
        }

        private string FindClassroomProcess()
        {
            var c = Context.Config.Classroom;
            if (!string.IsNullOrWhiteSpace(c.MainExecutable) && File.Exists(c.MainExecutable))
            {
                return Path.GetFileName(c.MainExecutable);
            }
            // 先问自动识别：它内部有可信度排序（正在运行的优先 → 学生端本体优先 → 云桌面客户端最后），
            // 所以不会被 ProcessNames 的书写顺序带偏。
            // 典型坑：噢易机房里 MultiClient.exe 也在跑，若按 ProcessNames 顺序先撞上它，
            // 就会把"关键客户端"错认成 MultiClient，Student.exe 被杀时反而没人管。
            string reason;
            var best = NicRoles_Detect(out reason);
            if (best != null)
            {
                Log.Info("自动识别课堂软件：" + reason);
                return Path.GetFileName(best);
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

        private static string NicRoles_Detect(out string reason)
        {
            var best = Core.Interop.ClassroomDetector.DetectBest(out reason);
            return best?.Path;
        }

        private ViolationAction HostAction()
        {
            var n = Context.Config.Network;
            if (n.ShutdownOnViolation) return ViolationAction.Shutdown;
            if (n.LockOnViolation) return ViolationAction.Lock;
            return ViolationAction.Notify;
        }

        /// <summary>
        /// 拉起"缺失的那个关键客户端"。红队 A5 教训：**绝不能退化成"随便拉一个课堂软件"**——
        /// 旧的 DetectBest 会挑中正在运行的 VoiClient（云桌面客户端），对它空转拉起 3 分钟，
        /// 而真缺的 Student.exe 始终没人管。目标必须锚定缺失进程的名字。
        /// </summary>
        private void Relaunch(string missingProcName)
        {
            if (DateTime.Now - _lastRelaunchAttempt < RelaunchThrottle) return;
            _lastRelaunchAttempt = DateTime.Now;

            var c = Context.Config.Classroom;
            string path = ResolveRelaunchPath(missingProcName, c.MainExecutable, ClassroomDetector.DetectAll());
            if (path == null)
            {
                Log.Error("关键客户端 " + missingProcName + " 缺失，但在本机找不到它的安装路径，无法拉起"
                    + "——请在设置里手动填写学生端程序路径");
                return;
            }

            if (Log.DryRun)
            {
                Log.Warn("[dry] 将重新启动电子教室客户端 " + path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Warn("已重新启动电子教室客户端 " + path + "（缺失进程：" + missingProcName + "）");
            }
            catch (Exception ex) { Log.Warn("重启电子教室客户端失败：" + ex.Message); }
        }

        /// <summary>
        /// 为"缺失的进程名"解析应拉起的完整路径（纯判定，便于自检）：
        /// 1) 设置里手填的 MainExecutable，但**仅当文件名与缺失进程一致**才可用；
        /// 2) 识别候选里按进程名匹配（磁盘证据优先于"正在运行"——它现在恰恰不在运行）；
        /// 3) 都找不到返回 null（调用方记录 ERROR，**绝不允许**拿别的进程顶替）。
        /// </summary>
        public static string ResolveRelaunchPath(string missingProcName, string mainExecutable,
            IEnumerable<ClassroomCandidate> candidates)
        {
            if (string.IsNullOrWhiteSpace(missingProcName)) return null;
            if (!string.IsNullOrWhiteSpace(mainExecutable)
                && string.Equals(Path.GetFileName(mainExecutable), missingProcName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(mainExecutable))
            {
                return mainExecutable;
            }
            var match = (candidates ?? Enumerable.Empty<ClassroomCandidate>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Path) && File.Exists(c.Path)
                    && string.Equals(
                        string.IsNullOrWhiteSpace(c.ProcessName) ? Path.GetFileName(c.Path) : c.ProcessName,
                        missingProcName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Source == "正在运行" ? 1 : 0)
                .FirstOrDefault();
            return match == null ? null : match.Path;
        }

        protected override void OnStop() { }
    }
}
