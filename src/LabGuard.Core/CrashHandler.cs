using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using LabGuard.Core.Logging;

namespace LabGuard.Core
{
    /// <summary>
    /// 统一崩溃兜底：任何未处理异常都会**写一份崩溃日志**（默认 %ProgramData%\LabGuard\logs\crash-*.log，
    /// 写不进去就退回 %TEMP%），并弹一个说明框——**不让程序"闪退"得无声无息**。
    /// </summary>
    public static class CrashHandler
    {
        public static void Install(string appName)
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Report(appName, e.Exception, false);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(appName, e.ExceptionObject as Exception, true);
            }
            catch { }
        }

        /// <summary>把整个入口包起来：异常不再"闪退"，而是留证据 + 提示。</summary>
        public static void Run(string appName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Report(appName, ex, true);
            }
        }

        public static void Report(string appName, Exception ex, bool fatal)
        {
            string detail = ex == null ? "(未知异常)" : ex.ToString();
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string path = WriteLog(appName, time, detail);
            Log.Error(appName + " 未处理异常" + (fatal ? "（致命）" : "") + "：" + detail);
            try
            {
                string msg =
                    "【" + appName + "】运行中出现错误，已记录日志：" + Environment.NewLine +
                    path + Environment.NewLine + Environment.NewLine +
                    (ex == null ? "" : ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine) +
                    "如果反复出现，请把这份日志发给管理员。" + Environment.NewLine +
                    "（按 Ctrl+Alt+Del 注销后可以临时停掉管控）";
                MessageBox.Show(msg, "LabGuard", MessageBoxButtons.OK,
                    fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
            }
            catch { }
        }

        private static string WriteLog(string appName, string time, string detail)
        {
            string file = "crash-" + DateTime.Now.ToString("yyyyMMdd") + ".log";
            var sb = new StringBuilder();
            sb.AppendLine("=== " + time + "  " + appName + " ===");
            sb.AppendLine("OS: " + Environment.OSVersion + "  User: " + Environment.UserName +
                          "  Admin: " + (IsAdmin() ? "yes" : "no"));
            sb.AppendLine("BaseDir: " + AppDomain.CurrentDomain.BaseDirectory);
            sb.AppendLine(detail);
            sb.AppendLine();
            foreach (string candidate in new[]
            {
                Path.Combine(Config.ConfigStore.DataDir, "logs"),
                Path.Combine(Path.GetTempPath(), "LabGuard-crash")
            })
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    string p = Path.Combine(candidate, file);
                    File.AppendAllText(p, sb.ToString(), new UTF8Encoding(false));
                    return p;
                }
                catch { }
            }
            return "(日志写入失败，请检查磁盘权限)";
        }

        private static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
