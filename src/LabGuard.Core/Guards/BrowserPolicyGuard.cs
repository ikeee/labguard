using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 浏览器管控（：只写组策略注册表，不动浏览器本体）：
    /// Chrome 小恐龙/下载、Edge 冲浪游戏/下载/开发者工具、Firefox 下载、IE 下载与另存为。
    /// 另可选"快捷方式接管"：把桌面/开始菜单里的浏览器快捷方式改指向本程序的 Launcher。
    /// </summary>
    public sealed class BrowserPolicyGuard : GuardBase
    {
        public override string Name => "浏览器策略";
        public override bool Enabled => Context?.Config.Browser.Enabled ?? false;
        protected override int IntervalMs => 30000;

        private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
        private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
        private const string Firefox = @"SOFTWARE\Policies\Mozilla\Firefox";
        private const string FirefoxDoh = @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS";
        private const string WindowsDnsClient = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
        private const string IeZones = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3";
        private const string IeRestrict = @"SOFTWARE\Policies\Microsoft\Internet Explorer\Restrictions";

        protected override void OnStart()
        {
            Apply();
            if (Context.Config.Browser.HijackShortcuts) HijackShortcuts();
        }

        protected override void OnTick()
        {
            var b = Context.Config.Browser;
            bool ok = true;
            if (b.BlockChromeDino)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.LocalMachine, Chrome, "AllowDinosaurEasterEgg") ?? 1) == 0;
            if (b.BlockDownloads)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.LocalMachine, Chrome, "DownloadRestrictions") ?? 0) == 3;
            if (!ok)
            {
                Context.Report(Name, "你违规修改了注册表的禁止下载/禁止游戏项！现在助手强制恢复！", ViolationAction.Notify);
                Apply();
            }
            SetStatus(ok ? "策略生效中" : "已恢复被改动的策略");
        }

        private void Apply()
        {
            var b = Context.Config.Browser;
            if (b.BlockDownloads)
            {
                Set(Chrome, "DownloadRestrictions", 3);
                Set(Edge, "DownloadRestrictions", 3);
                Set(Firefox, "DisableDownloads", 1);
                Set(Firefox, "BlockAboutDownloads", 1);
                Set(IeZones, "1803", 3);
                Set(IeZones, "2200", 0);
            }
            if (b.BlockSaveAs)
            {
                Set(IeRestrict, "NoBrowserSaveAs", 1);
                Set(Edge, "SaveAs", 0);
            }
            if (b.BlockDevTools) Set(Edge, "DeveloperToolsAvailability", 2);
            if (b.BlockChromeDino) Set(Chrome, "AllowDinosaurEasterEgg", 0);
            if (b.BlockEdgeSurf) Set(Edge, "AllowSurfGame", 0);

            // 关闭浏览器自带加密 DNS（DoH）：否则学生开 DoH 就绕过教师机的 AdGuard 过滤
            if (Context.Config.Network.BlockDoh)
            {
                Set(Chrome, "DnsOverHttpsMode", "off");
                Set(Chrome, "BuiltInDnsClientEnabled", 0);
                Set(Edge, "DnsOverHttpsMode", "off");
                Set(Edge, "BuiltInDnsClientEnabled", 0);
                Set(FirefoxDoh, "Enabled", 0);
                Set(FirefoxDoh, "Locked", 1);
                Set(WindowsDnsClient, "DoHPolicy", 0); // Win11 系统级加密 DNS：0 = 不允许
                Log.Info("已关闭浏览器/系统加密 DNS（DoH），保证集中 DNS 过滤有效");
            }
            if (!string.IsNullOrWhiteSpace(b.Homepage)) Set(Chrome, "HomepageLocation", b.Homepage);
            Log.Info("已应用浏览器组策略（禁止下载=" + b.BlockDownloads + "，小恐龙=" + b.BlockChromeDino + "）");
        }

        private static void Set(string subKey, string name, object value)
        {
            SystemActions.SetRegistryValue(RegistryHive.LocalMachine, subKey, name, value,
                value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }

        /// <summary>用本程序的 Launcher 替换浏览器快捷方式（默认关闭，需显式开启）。</summary>
        private void HijackShortcuts()
        {
            string launcher = Path.Combine(Context.InstallDir, "LabGuard.Launcher.exe");
            if (!File.Exists(launcher))
            {
                Log.Warn("未找到 " + launcher + "，跳过快捷方式接管");
                return;
            }
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                @"C:\Users\Public\Desktop",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch")
            };
            string[] keys = { "chrome", "msedge", "firefox", "360", "2345", "qq", "jisu", "shuanghe", "edge", "谷歌浏览器" };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string lnk in SafeGetFiles(root, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(lnk);
                    bool hit = false;
                    foreach (string k in keys)
                    {
                        if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    }
                    if (!hit) continue;
                    RewriteShortcut(lnk, launcher, name);
                }
            }
        }

        private static IEnumerable<string> SafeGetFiles(string root, string pattern)
        {
            try { return Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
            catch { return new string[0]; }
        }

        private static void RewriteShortcut(string lnkPath, string target, string title)
        {
            if (Log.DryRun) { Log.Info("[dry] 将接管快捷方式 " + lnkPath + " -> " + target); return; }
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object shortcut = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type sc = shortcut.GetType();
                sc.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                sc.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { title + "（机房统一入口）" });
                sc.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Log.Info("已接管快捷方式：" + lnkPath);
            }
            catch (Exception ex) { Log.Warn("接管快捷方式失败 " + lnkPath + " :: " + ex.Message); }
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            foreach (string k in new[] { Chrome, Edge, Firefox, IeRestrict })
                SystemActions.DeleteRegistryKey(RegistryHive.LocalMachine, k);
            SystemActions.DeleteRegistryKey(RegistryHive.LocalMachine, FirefoxDoh);
            SystemActions.DeleteRegistryValue(RegistryHive.LocalMachine, WindowsDnsClient, "DoHPolicy");
            foreach (string v in new[] { "1803", "2200" })
                SystemActions.DeleteRegistryValue(RegistryHive.LocalMachine, IeZones, v);
            Log.Info("已移除浏览器组策略");
        }
    }
}
