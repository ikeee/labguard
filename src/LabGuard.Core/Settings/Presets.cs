using System;
using System.Collections.Generic;
using LabGuard.Core.Config;

namespace LabGuard.Core.Settings
{
    /// <summary>机房预设：点一下套用一组合理默认值，之后仍可逐项手改。</summary>
    public static class Presets
    {
        public sealed class Preset
        {
            public string Name;
            public string Description;
            public Action<GuardConfig> Apply;
        }

        public static List<Preset> All()
        {
            return new List<Preset>
            {
                new Preset
                {
                    Name = "普通 PC 机房（推荐）",
                    Description = "保护电子教室 + 集中 DNS 过滤 + 断网遮罩；不锁屏不关机，U 盘禁用",
                    Apply = ApplyCommon
                },
                new Preset
                {
                    Name = "一体机机房（有线为主，无线做热点）",
                    Description = "同上，另外只把有线当判据、忽略 WLAN/热点，热点开关不会误报",
                    Apply = c =>
                    {
                        ApplyCommon(c);
                        c.Network.WatchWiredOnly = true;
                        foreach (string extra in new[] { "WLAN", "无线", "Wi-Fi" })
                        {
                            if (!c.Network.ExcludedInterfaces.Contains(extra)) c.Network.ExcludedInterfaces.Add(extra);
                        }
                    }
                },
                new Preset
                {
                    Name = "只管电子教室，其它不干预",
                    Description = "只做电子教室保护 + 断网提示（不弹遮罩）；U 盘/下载/浏览器/任务栏全部放开",
                    Apply = c =>
                    {
                        c.Enabled = true;
                        c.Classroom.Enabled = true;
                        c.Classroom.ResumeWhenSuspended = true;
                        c.Classroom.RelaunchWhenKilled = true;
                        c.Network.Enabled = true;
                        c.Network.DetectDisconnected = true;
                        c.Network.DisconnectMask = false;
                        c.Network.DisconnectSoundAfterSeconds = 0;
                        c.Network.EnforceDns = false;
                        c.ProcessBlock.Enabled = false;
                        c.FileCreation.Enabled = false;
                        c.Usb.AllowStorage = true;
                        c.Hosts.Enabled = false;
                        c.Hosts.ProtectFileAcl = false;
                        c.Browser.Enabled = false;
                        c.Shell.Enabled = false;
                        c.SafeMode.Enabled = false;
                        c.Wallpaper.Enabled = false;
                        c.RegistryAcl.Enabled = false;
                    }
                },
                new Preset
                {
                    Name = "全部放开（仅安装，不启用任何管控）",
                    Description = "先把程序装好、策略全部关闭，课上需要时再逐项打开",
                    Apply = c =>
                    {
                        c.Enabled = false;
                        c.Classroom.Enabled = false;
                        c.Network.Enabled = false;
                        c.ProcessBlock.Enabled = false;
                        c.FileCreation.Enabled = false;
                        c.Usb.Enabled = false;
                        c.Hosts.Enabled = false;
                        c.Hosts.ProtectFileAcl = false;
                        c.Browser.Enabled = false;
                        c.Shell.Enabled = false;
                        c.SafeMode.Enabled = false;
                        c.Wallpaper.Enabled = false;
                        c.RegistryAcl.Enabled = false;
                        c.Watchdog.Enabled = false;
                    }
                }
            };
        }

        private static void ApplyCommon(GuardConfig c)
        {
            c.Enabled = true;
            c.StartDelaySeconds = 120;
            c.Classroom.Enabled = true;
            c.Classroom.ResumeWhenSuspended = true;
            c.Classroom.RelaunchWhenKilled = true;
            c.Classroom.GuardELearningParameters = true;
            c.Network.Enabled = true;
            c.Network.DetectDisconnected = true;
            c.Network.RestoreOriginalIp = true;
            c.Network.ForceFirewallOff = true;
            c.Network.DisconnectMask = true;
            c.Network.DisconnectMaskBackground = "Wallpaper";
            c.Network.DisconnectMaskShowElapsed = true;
            c.Network.DisconnectSoundAfterSeconds = 60;
            c.Network.DisconnectSoundTimes = 3;
            c.Network.DisconnectSoundIntervalMs = 700;
            c.Network.WatchWiredOnly = true;
            c.Network.DisconnectRequireAllDown = true;
            c.Network.EnforceDns = true;                 // 地址需按机房填
            c.Network.BlockDoh = true;
            c.Network.DnsLockOnlyWatchedInterfaces = true;
            c.Network.LockOnViolation = false;
            c.Network.ShutdownOnViolation = false;
            c.ProcessBlock.Enabled = true;
            c.ProcessBlock.Action = "Kill";
            c.FileCreation.Enabled = true;
            c.FileCreation.Mode = "CppOnly";
            c.FileCreation.Action = "Delete";
            c.Usb.Enabled = true;
            c.Usb.AllowStorage = false;
            c.Hosts.Enabled = false;
            c.Hosts.ProtectFileAcl = true;
            c.Browser.Enabled = true;
            c.Shell.Enabled = true;
            c.SafeMode.Enabled = true;
            c.Wallpaper.Enabled = true;
            c.Wallpaper.ShowMachineNumber = true;
            c.RegistryAcl.Enabled = true;
            c.Watchdog.Enabled = true;
            c.Watchdog.RebootOnServiceFailure = false;
        }
    }
}
