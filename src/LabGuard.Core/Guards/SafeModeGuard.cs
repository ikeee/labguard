using System;
using System.IO;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 安全模式与启动菜单（原版）：删除 SafeBoot\Network（禁止"带网络连接的安全模式"）、隐藏启动菜单。
    /// 删除前会把整棵子树导出成 .reg 备份，退出时还原。
    /// </summary>
    public sealed class SafeModeGuard : GuardBase
    {
        public override string Name => "安全模式/启动菜单";
        public override bool Enabled => Context?.Config.SafeMode.Enabled ?? false;
        protected override int IntervalMs => 30000;

        private const string SafeBootNetwork = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network";
        private string BackupReg => Path.Combine(Config.ConfigStore.DataDir, "safeboot-network.reg");

        protected override void OnStart()
        {
            if (Context.Config.SafeMode.BlockNetworkSafeMode) RemoveSafeBootNetwork();
            if (Context.Config.SafeMode.HideBootMenu)
            {
                SystemActions.Run("bcdedit.exe", "/set {bootmgr} displaybootmenu no");
            }
        }

        protected override void OnTick()
        {
            bool removed = false;
            if (Context.Config.SafeMode.BlockNetworkSafeMode &&
                SystemActions.RegistryKeyExists(RegistryHive.LocalMachine, SafeBootNetwork))
            {
                Context.Report(Name, "检测到带网络连接的安全模式被恢复，已重新禁用。", ViolationAction.Notify);
                RemoveSafeBootNetwork();
                removed = true;
            }
            SetStatus(removed ? "已重新禁用带网络的安全模式" : "已禁用带网络的安全模式");
        }

        private void RemoveSafeBootNetwork()
        {
            try
            {
                if (!SystemActions.RegistryKeyExists(RegistryHive.LocalMachine, SafeBootNetwork)) return;
                if (!Log.DryRun && !File.Exists(BackupReg))
                {
                    Directory.CreateDirectory(Config.ConfigStore.DataDir);
                    SystemActions.Run("reg.exe",
                        "export \"HKLM\\" + SafeBootNetwork + "\" \"" + BackupReg + "\" /y");
                }
                SystemActions.DeleteRegistryKey(RegistryHive.LocalMachine, SafeBootNetwork);
                Log.Warn("已删除 SafeBoot\\Network（禁止带网络的安全模式）");
            }
            catch (Exception ex) { Log.Warn("处理 SafeBoot\\Network 失败：" + ex.Message); }
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            if (Context.Config.SafeMode.HideBootMenu)
            {
                // 删除该值 = 恢复 Windows 默认（不显示旧式启动菜单），比强制设成 yes 更"还原"
                SystemActions.Run("bcdedit.exe", "/deletevalue {bootmgr} displaybootmenu");
            }
            if (Context.Config.SafeMode.BlockNetworkSafeMode && File.Exists(BackupReg))
            {
                SystemActions.Run("reg.exe", "import \"" + BackupReg + "\"");
                Log.Info("已还原 SafeBoot\\Network");
            }
        }
    }
}
