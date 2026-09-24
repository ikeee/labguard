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
        public static string Directory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuard", "logs");
        public static LogLevel MinLevel { get; set; } = LogLevel.Info;
        public static bool EchoToConsole { get; set; }
        /// <summary>干跑（模拟）模式：凡是会改动系统的动作都不会真正执行。</summary>
        public static bool DryRun { get; set; }

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
                    File.AppendAllText(Path.Combine(Directory, "guard-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志写不进去也不能影响管控逻辑
                }
            }
        }
    }
}
