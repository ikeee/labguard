using System;
using System.IO;
using System.Text;

namespace LabGuard.Core.Logging
{
    public enum LogLevel { Debug, Info, Warn, Error }

    /// <summary>极简滚动日志。默认写到 %ProgramData%\LabGuard\logs\guard-YYYYMMDD.log。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _currentPath;
        private static long _expectedLength = -1;
        /// <summary>本进程观测到的"日志曾被清空/删除"次数（心跳可带上）。</summary>
        public static int TamperEvents { get; private set; }
        public static string Directory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuard", "logs");
        public static LogLevel MinLevel { get; set; } = LogLevel.Info;
        public static bool EchoToConsole { get; set; }

        private static bool _dryRun;
        private static bool _dryRunForced;

        /// <summary>
        /// 干跑（模拟）模式：凡是会改动系统的动作都不会真正执行。
        ///
        /// 注意：**可以被人改成 false**（<see cref="Guards.GuardEngine.Start"/> 就按调用方传的
        /// dryRun 覆盖它）。所以"测试/沙箱"这种**必须**干跑的场合要用 <see cref="ForceDryRunOn"/>。
        /// </summary>
        public static bool DryRun { get { return _dryRun || _dryRunForced; } set { _dryRun = value; } }

        /// <summary>
        /// 一旦置位，本进程**永远**是干跑：任何 `DryRun = false` 都改不回来。
        ///
        /// 为什么需要（2026-10-13 真机事故复盘）：测试台用 `--data-dir` 起的沙箱服务是
        /// `GuardService` → `GuardEngine.Start(..., dryRun: false, EngineRole.SystemCore)`，
        /// 那一句会把沙箱刚设好的 DryRun 直接掀掉，于是沙箱进程按真策略改真机
        /// （U 盘禁用、浏览器策略、启动菜单……）。必须有一个"谁也改不掉"的总闸。
        /// </summary>
        public static void ForceDryRunOn()
        {
            _dryRunForced = true;
            Warn("已锁定为干跑模式（本进程不会改动系统）");
        }

        public static bool DryRunForced { get { return _dryRunForced; } }

        public static void Debug(string msg) => Write(LogLevel.Debug, msg);
        public static void Info(string msg) => Write(LogLevel.Info, msg);
        public static void Warn(string msg) => Write(LogLevel.Warn, msg);
        public static void Error(string msg) => Write(LogLevel.Error, msg);

        public static void Error(string msg, Exception ex) => Write(LogLevel.Error, msg + " :: " + ex);

        public static void Write(LogLevel level, string msg)
        {
            if (level < MinLevel) return;
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [{2}] {3}",
                DateTime.Now, level.ToString().ToUpperInvariant(), Environment.UserName, msg);
            lock (Gate)
            {
                if (EchoToConsole)
                {
                    Console.WriteLine(line);
                }
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    string path = Path.Combine(Directory, "guard-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    // 红队 D5：日志能被无声清空/删除 = 毁尸灭迹零成本。
                    // 常驻句柄方案在多进程（服务+小助手）同时写日志时行不通（共享模式互相挡），
                    // 改为"自愈式留痕"：发现文件短于上次写入后的长度（或干脆没了），
                    // 先补一行毁迹标记再写正行——攻击者可以删历史，但删不掉"他删过"这个事实。
                    if (path == _currentPath && _expectedLength >= 0)
                    {
                        long actual = File.Exists(path) ? new FileInfo(path).Length : -1;
                        if (actual < _expectedLength)
                        {
                            TamperEvents++;
                            string marker = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [WARN ] [{1}] 日志文件曾被清空或删除（此前约 {2} 字节的记录丢失）——可能有人在毁尸灭迹",
                                DateTime.Now, Environment.UserName, _expectedLength);
                            File.AppendAllText(path, marker + Environment.NewLine, new UTF8Encoding(false));
                            if (EchoToConsole) Console.WriteLine(marker);
                        }
                    }
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                    _currentPath = path;
                    try { _expectedLength = new FileInfo(path).Length; } catch { _expectedLength = -1; }
                }
                catch
                {
                    // 日志写不进去也不能影响管控逻辑
                }
            }
        }
    }
}
