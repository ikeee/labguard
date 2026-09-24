using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Launcher
{
    /// <summary>
    /// "上网入口"（对应原版 zy 目录里的假浏览器，但用途说明写清楚、不冒充其它软件）：
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
            string browser = FindBrowser();
            if (browser == null)
            {
                MessageBox.Show("本机未找到可用的浏览器，请联系机房管理员。", "机房上网入口",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string homepage = ConfigStore.Load().Browser.Homepage;
            string arguments = string.IsNullOrWhiteSpace(homepage) ? "" : homepage;
            if (args != null && args.Length > 0 && args[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                arguments = args[0];
            }
            try
            {
                Process.Start(new ProcessStartInfo(browser, arguments) { UseShellExecute = false });
                Log.Info("上网入口启动浏览器：" + browser + " " + arguments);
            }
            catch (Exception ex)
            {
                Log.Warn("启动浏览器失败：" + ex.Message);
                MessageBox.Show("启动浏览器失败：" + ex.Message, "机房上网入口");
            }
        }

        private static string FindBrowser()
        {
            foreach (string c in Candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
