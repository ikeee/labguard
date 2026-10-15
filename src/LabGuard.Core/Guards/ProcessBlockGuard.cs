using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using LabGuard.Core.Data;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 违规软件拦截（进程名 + 窗口标题双表匹配）：
    /// 破解/脱控工具、进程工具、虚拟桌面、杀软管家、解压软件、小游戏、系统工具。
    /// 同时对小游戏与几条高危命令写 IFEO 的 Debugger=null（映像劫持），退出时清掉。
    /// </summary>
    public sealed class ProcessBlockGuard : GuardBase
    {
        public override string Name => "违规软件拦截";
        /// <summary>
        /// 本模块什么时候跑：① 「违规软件拦截」组开着；**或**② 自保的「拦杀进程工具」开着。
        /// ② 是 2026-10-15 加的：出厂口径"自保全开 + 管控全关"下，那个组默认是关的，
        /// 只靠 ① 的话"学生拿 taskkill 杀不掉"就成了一句空话（见 KeywordsFor 的注释）。
        /// </summary>
        public override bool Enabled =>
            Context == null ? false :
            (Context.Config.ProcessBlock.Enabled ||
             (Context.Config.AntiTamper.Enabled && Context.Config.AntiTamper.BlockKillTools));
        protected override int IntervalMs => 2500;

        private readonly Dictionary<string, DateTime> _recent = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private string _ownProcess;

        protected override void OnStart()
        {
            _ownProcess = Process.GetCurrentProcess().ProcessName;
            ApplyIfeo();
        }

        protected override void OnTick()
        {
            var keywords = BuildKeywords();
            var hits = new Dictionary<string, Tuple<Process, string>>(StringComparer.OrdinalIgnoreCase);

            // 1) 按进程名/模块名匹配
            foreach (Process p in Process.GetProcesses())
            {
                string name;
                try
                {
                    name = p.ProcessName;
                    if (string.Equals(name, _ownProcess, StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsClassroom(name)) continue;
                    if (name.StartsWith("LabGuard", StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch { p.Dispose(); continue; }

                string match = Match(keywords, name + ".exe");
                if (match == null)
                {
                    string title = MainWindowTitleSafe(p);
                    if (title != null) match = Match(keywords, title);
                }
                if (match != null && !hits.ContainsKey(match)) hits[match] = Tuple.Create(p, name);
                else p.Dispose();
            }

            // 2) 按前台窗口标题匹配（有些工具进程名看不出，比如"任务管理器"）
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(fg, out pid);
                int len = NativeMethods.GetWindowTextLength(fg);
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 2);
                    NativeMethods.GetWindowText(fg, sb, sb.Capacity);
                    string title = sb.ToString();
                    string match = Match(keywords, title);
                    if (match != null && !hits.ContainsKey(match))
                    {
                        try
                        {
                            Process p = Process.GetProcessById((int)pid);
                            hits[match] = Tuple.Create(p, p.ProcessName);
                        }
                        catch { }
                    }
                }
            }

            if (hits.Count == 0) { SetStatus("监控中"); return; }

            foreach (var kv in hits)
            {
                string keyword = kv.Key;
                Process p = kv.Value.Item1;
                string name = kv.Value.Item2;
                DateTime last;
                if (_recent.TryGetValue(keyword, out last) && (DateTime.Now - last).TotalSeconds < 10)
                {
                    p.Dispose();
                    continue;
                }
                _recent[keyword] = DateTime.Now;

                string message = MessageFor(keyword);
                Context.Report(Name, message, Action(), name + " / 关键特征：" + keyword);
                if (Action() != ViolationAction.Notify) SystemActions.KillProcess(p);
                SetStatus("已处置：" + name);
                p.Dispose();
            }
        }

        private ViolationAction Action()
        {
            switch ((Context.Config.ProcessBlock.Action ?? "Kill").Trim().ToUpperInvariant())
            {
                case "NOTIFY": return ViolationAction.Notify;
                case "LOCK": return ViolationAction.Lock;
                case "SHUTDOWN": return ViolationAction.Shutdown;
                case "REBOOT": return ViolationAction.Reboot;
                default: return ViolationAction.Kill;
            }
        }

        private static string MessageFor(string keyword)
        {
            foreach (string s in DefaultBlocklists.Archivers)
            {
                if (keyword.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "本机禁止使用解压缩软件；如确有需要，请老师用密码暂停管控。";
            }
            foreach (string s in DefaultBlocklists.AntiVirus)
            {
                if (keyword.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "检测到杀毒/虚拟机类软件在运行，可能影响课堂广播；如为误判请老师调整策略。";
            }
            return "检测到当前运行的软件不符合机房策略；如为误判请老师调整拦截清单。" +
                   "注：电脑死机时不能用任务管理器，可同时按 ctrl+alt+del，点【注销】，电脑不会自动还原。";
        }

        /// <summary>
        /// 算"这一轮要拦哪些关键词"。**纯函数**（只吃配置，不碰 Context/系统），自检可以直接断言。
        ///
        /// 2026-10-15 起的分界：**自保类关键词不受「违规软件拦截」组总闸控制**。
        /// 起因：出厂口径是"自保全开 + 管控全关"，而「违规软件拦截」组默认是关的 ——
        /// 如果"拦住 taskkill / Process Hacker"这句话还要挂在那个总闸上，面板上看着开了、
        /// 实际上一条都没拦（正是「开了但不会生效」的典型）。所以：**组关着时只拦杀进程工具**。
        /// </summary>
        public static List<string> KeywordsFor(Config.GuardConfig config)
        {
            var cfg = config.ProcessBlock;
            var list = new List<string>();
            // —— 管控类：整组受「违规软件拦截」总闸控制（组关着时一条都不加）——
            if (cfg.Enabled)
            {
                if (cfg.BlockClassroomCrack) list.AddRange(DefaultBlocklists.ClassroomCrack);
                if (cfg.BlockProcessTools) list.AddRange(DefaultBlocklists.ProcessTools);
                if (cfg.BlockVirtualDesktop) list.AddRange(DefaultBlocklists.VirtualDesktop);
                if (cfg.BlockAntiVirus) list.AddRange(DefaultBlocklists.AntiVirus);
                if (cfg.BlockArchivers) list.AddRange(DefaultBlocklists.Archivers);
                if (cfg.BlockGames) list.AddRange(DefaultBlocklists.Games);
                if (cfg.BlockTaskManager) list.Add("任务管理器");
                if (cfg.BlockRegedit) list.Add("注册表编辑器");
                if (cfg.BlockCommandPrompt)
                {
                    list.Add("Windows 命令处理程序");
                    list.Add("Windows Command Processor");
                    list.Add("Windows PowerShell");
                }
                foreach (string extra in cfg.ExtraKeywords)
                {
                    foreach (string one in (extra ?? "").Split(new[] { ',', ';', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        list.Add(one.Trim());
                }
            }
            // —— 自保类：不受上面那个总闸控制（见方法注释）——
            if (config.AntiTamper.Enabled && config.AntiTamper.BlockKillTools)
                list.AddRange(DefaultBlocklists.KillTools);
            return list;
        }

        private List<string> BuildKeywords() { return KeywordsFor(Context.Config); }

        private bool IsClassroom(string processName)
        {
            foreach (string n in Context.Config.Classroom.ProcessNames)
            {
                string bare = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
                if (string.Equals(bare, processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string MainWindowTitleSafe(Process p)
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) return null;
                return p.MainWindowTitle;
            }
            catch { return null; }
        }

        private static string Match(IEnumerable<string> keywords, string haystack)
        {
            if (string.IsNullOrEmpty(haystack)) return null;
            foreach (string k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (haystack.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return k;
            }
            return null;
        }

        // ------------------------------------------------------------------ IFEO 映像劫持
        private void ApplyIfeo()
        {
            var cfg = Context.Config.ProcessBlock;
            // 组关着就**一个字都不写**：IFEO 是"让游戏/破解工具起不来"的管控动作，
            // 而自保（拦杀进程工具）只需要关键词拦截，不需要动注册表。
            // （这条是被自检"全关配置起引擎再停止 → 一条系统写入都没有"抓出来的真回归。）
            if (!cfg.Enabled) return;
            if (cfg.BlockGames)
            {
                foreach (string g in DefaultBlocklists.GamesIfeo)
                    WriteIfeo(g);
            }
            if (cfg.BlockClassroomCrack)
            {
                foreach (string c in DefaultBlocklists.BlockedCommandsIfeo)
                    WriteIfeo(c);
            }
            // 只有真的写过 IFEO 才算（否则 Stop() 时不该去删 IFEO 项）
            if (cfg.BlockGames || cfg.BlockClassroomCrack) MarkApplied();
        }

        private static void WriteIfeo(string exeName)
        {
            string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + exeName;
            SystemActions.SetRegistryValue(RegistryHive.LocalMachine, key, "Debugger", "null", RegistryValueKind.String);
        }

        private static void RemoveIfeo(string exeName)
        {
            string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + exeName;
            SystemActions.DeleteRegistryValue(RegistryHive.LocalMachine, key, "Debugger");
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            foreach (string g in DefaultBlocklists.GamesIfeo) RemoveIfeo(g);
            foreach (string c in DefaultBlocklists.BlockedCommandsIfeo) RemoveIfeo(c);
        }
    }
}
