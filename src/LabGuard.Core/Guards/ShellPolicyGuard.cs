using System;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 任务栏 / 系统工具策略（原版 win_jian、gaowei、zcb 等）：
    /// 隐藏任务视图（虚拟桌面入口）、禁任务栏右键、禁任务管理器/注册表编辑器/cmd、
    /// 隐藏文件扩展名与文件夹选项、可选禁止锁定工作站。
    /// </summary>
    public sealed class ShellPolicyGuard : GuardBase
    {
        public override string Name => "任务栏/系统工具";
        public override bool Enabled => Context?.Config.Shell.Enabled ?? false;
        protected override int IntervalMs => 30000;

        private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string PoliciesSystem = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string PoliciesExplorer = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        private const string PoliciesWinSystem = @"Software\Policies\Microsoft\Windows\System";

        protected override void OnStart() => Apply();

        protected override void OnTick()
        {
            var s = Context.Config.Shell;
            bool ok = true;
            if (s.HideTaskView)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "ShowTaskViewButton") ?? 1) == 0;
            if (s.DisableTaskManager)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableTaskMgr") ?? 0) == 1;
            if (!ok)
            {
                Context.Report(Name, "你修改了小助手设置！现在助手强制恢复！", ViolationAction.Notify);
                Apply();
            }
            SetStatus(ok ? "策略生效中" : "已恢复被改动的策略");
        }

        private void Apply()
        {
            var s = Context.Config.Shell;
            if (s.HideTaskView)
            {
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "ShowTaskViewButton", 0, RegistryValueKind.DWord);
            }
            if (s.NoTrayContextMenu)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, PoliciesExplorer, "NoTrayContextMenu", 1, RegistryValueKind.DWord);
            if (s.DisableTaskManager)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableTaskMgr", 1, RegistryValueKind.DWord);
            if (s.DisableRegistryTools)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableRegistryTools", 1, RegistryValueKind.DWord);
            if (s.DisableCmd)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, PoliciesWinSystem, "DisableCMD", 1, RegistryValueKind.DWord);
            if (s.DisableLockWorkstation)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableLockWorkstation", 1, RegistryValueKind.DWord);
            if (s.HideFileExtensions)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "HideFileExt", 1, RegistryValueKind.DWord);
            if (s.NoFolderOptions)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "NoFolderOptions", 1, RegistryValueKind.DWord);
            Log.Info("已应用任务栏/系统工具策略");
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            var s = Context.Config.Shell;
            if (s.HideTaskView)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "ShowTaskViewButton", 1, RegistryValueKind.DWord);
            if (s.NoTrayContextMenu) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, PoliciesExplorer, "NoTrayContextMenu");
            if (s.DisableTaskManager) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableTaskMgr");
            if (s.DisableRegistryTools) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableRegistryTools");
            if (s.DisableCmd) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, PoliciesWinSystem, "DisableCMD");
            if (s.DisableLockWorkstation) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, PoliciesSystem, "DisableLockWorkstation");
            if (s.HideFileExtensions)
                SystemActions.SetRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "HideFileExt", 0, RegistryValueKind.DWord);
            if (s.NoFolderOptions) SystemActions.DeleteRegistryValue(RegistryHive.CurrentUser, ExplorerAdvanced, "NoFolderOptions");
            Log.Info("已还原任务栏/系统工具策略");
        }
    }
}
