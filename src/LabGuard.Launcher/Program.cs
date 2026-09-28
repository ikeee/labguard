using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Launcher
{
    /// <summary>
    /// "上网入口"：按配置的主页启动学生机上的真实浏览器（用途写清楚，不冒充其它软件）：
    /// 找真实浏览器启动，并把设置里填写的导航页/主页作为参数带上；未配置主页时等价于直接开浏览器。
    /// </summary>
    internal static class Program
    {
        private static readonly string[] Candidates =
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"
        };

        [STAThread]
        private static void Main(string[] args)
        {
            GuardConfig config = ConfigStore.Load();
            string browser = FindBrowser(config.Browser.LauncherBrowser, out string source);
            if (browser == null)
            {
                MessageBox.Show("本机未找到可用的浏览器，请联系机房管理员。", "机房上网入口",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string homepage = config.Browser.Homepage;
            string arguments = string.IsNullOrWhiteSpace(homepage) ? "" : homepage;
            if (args != null && args.Length > 0 && args[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                arguments = args[0];
            }
            try
            {
                Process.Start(new ProcessStartInfo(browser, arguments) { UseShellExecute = false });
                Log.Info("上网入口启动浏览器（来源：" + source + "）：" + browser + " " + arguments);
            }
            catch (Exception ex)
            {
                Log.Warn("启动浏览器失败：" + ex.Message);
                MessageBox.Show("启动浏览器失败：" + ex.Message, "机房上网入口");
            }
        }

        /// <summary>
        /// 找真实浏览器，优先级：设置里指定的路径 → 注册表登记的默认浏览器 →
        /// App Paths（chrome/msedge/firefox）→ 常见安装路径。
        /// 每一步都校验文件真实存在，装得再偏也能找到。
        /// </summary>
        private static string FindBrowser(string configured, out string source)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string p = configured.Trim().Trim('"');
                if (File.Exists(p)) { source = "设置指定"; return p; }
                Log.Warn("设置里指定的浏览器不存在，改走自动探测：" + p);
            }

            string fromClients = DefaultBrowserFromClients();
            if (fromClients != null) { source = "注册表默认浏览器"; return fromClients; }

            foreach (string exe in new[] { "chrome.exe", "msedge.exe", "firefox.exe" })
            {
                string ap = AppPaths(exe);
                if (ap != null) { source = "App Paths/" + exe; return ap; }
            }

            foreach (string c in Candidates)
            {
                if (File.Exists(c)) { source = "常见安装路径"; return c; }
            }
            source = "未找到";
            return null;
        }

        /// <summary>读"默认浏览器"注册信息（HKLM/HKCU 的 Clients\StartMenuInternet\*），解析出 exe 路径。</summary>
        private static string DefaultBrowserFromClients()
        {
            foreach (bool machine in new[] { false, true })
            {
                try
                {
                    using (RegistryKey root = machine ? Registry.LocalMachine : Registry.CurrentUser)
                    using (RegistryKey clients = root?.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet"))
                    {
                        if (clients == null) continue;
                        foreach (string name in clients.GetSubKeyNames())
                        {
                            using (RegistryKey cmd = clients.OpenSubKey(name + @"\shell\open\command"))
                            {
                                string path = ExtractExePath(cmd?.GetValue(null) as string);
                                if (path != null) return path;
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>从命令行里取出 exe 路径：`"C:\...\chrome.exe" -- "%1"` → C:\...\chrome.exe</summary>
        private static string ExtractExePath(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            string s = commandLine.Trim();
            string path;
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                if (end < 0) return null;
                path = s.Substring(1, end - 1);
            }
            else
            {
                int end = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return null;
                path = s.Substring(0, end + 4);
            }
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            return File.Exists(path) ? path : null;
        }

        /// <summary>读 App Paths（HKLM/HKCU）里登记的浏览器路径。</summary>
        private static string AppPaths(string exeName)
        {
            foreach (bool machine in new[] { true, false })
            {
                try
                {
                    using (RegistryKey root = machine ? Registry.LocalMachine : Registry.CurrentUser)
                    using (RegistryKey k = root?.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
                    {
                        string p = k?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
