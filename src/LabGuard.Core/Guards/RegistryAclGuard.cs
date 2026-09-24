using System;
using System.Security.AccessControl;
using System.Security.Principal;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 注册表权限加固（不使用外部工具，直接用 .NET 注册表权限 API）：
    /// 把配置键设为"普通用户只读、管理员/SYSTEM 完全控制"，使学生无法改配置。
    /// </summary>
    public sealed class RegistryAclGuard : GuardBase
    {
        public override string Name => "注册表权限加固";
        public override bool Enabled => Context?.Config.RegistryAcl.Enabled ?? false;
        protected override int IntervalMs => 60000;

        protected override void OnStart()
        {
            foreach (string key in Context.Config.RegistryAcl.Keys) Harden(key);
        }

        protected override void OnTick()
        {
            int ok = 0;
            foreach (string key in Context.Config.RegistryAcl.Keys)
            {
                if (IsHardened(key)) ok++;
                else { Harden(key); }
            }
            SetStatus(ok + "/" + Context.Config.RegistryAcl.Keys.Count + " 个键已加固");
        }

        internal static RegistryKey OpenBase(string fullKey, out string subKey)
        {
            subKey = fullKey;
            if (fullKey.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                subKey = fullKey.Substring("HKEY_LOCAL_MACHINE\\".Length);
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            }
            if (fullKey.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                subKey = fullKey.Substring("HKEY_CURRENT_USER\\".Length);
                return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            }
            return null;
        }

        private bool IsHardened(string fullKey)
        {
            try
            {
                string sub;
                using (RegistryKey baseKey = OpenBase(fullKey, out sub))
                {
                    if (baseKey == null) return false;
                    using (RegistryKey key = baseKey.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions))
                    {
                        if (key == null) return false;
                        RegistrySecurity sec = key.GetAccessControl(AccessControlSections.Access);
                        foreach (RegistryAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        {
                            if (rule.AccessControlType != AccessControlType.Allow) continue;
                            var sid = (SecurityIdentifier)rule.IdentityReference;
                            bool isUsers = sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
                                           sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
                                           sid.IsWellKnown(WellKnownSidType.WorldSid);
                            if (isUsers && (rule.RegistryRights & RegistryRights.SetValue) == RegistryRights.SetValue) return false;
                        }
                        return true;
                    }
                }
            }
            catch { return false; }
        }

        private void Harden(string fullKey)
        {
            if (Log.DryRun)
            {
                Log.Info("[dry] 将加固注册表权限：" + fullKey + "（拒绝 Builtin\\Users 写入）");
                return;
            }
            try
            {
                string sub;
                using (RegistryKey baseKey = OpenBase(fullKey, out sub))
                {
                    if (baseKey == null) { Log.Warn("不支持的注册表根：" + fullKey); return; }
                    using (RegistryKey key = baseKey.CreateSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree))
                    {
                        if (key == null) return;
                        RegistrySecurity sec = key.GetAccessControl(AccessControlSections.Access);
                        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                        // 拒绝普通用户写入（Administrators / SYSTEM 不受影响，仍可退出后改回）
                        sec.AddAccessRule(new RegistryAccessRule(users,
                            RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete,
                            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Deny));
                        key.SetAccessControl(sec);
                        Log.Info("已加固注册表权限：" + fullKey);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("加固注册表权限失败 " + fullKey + " :: " + ex.Message); }
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            foreach (string fullKey in Context.Config.RegistryAcl.Keys)
            {
                try
                {
                    string sub;
                    using (RegistryKey baseKey = OpenBase(fullKey, out sub))
                    {
                        if (baseKey == null) continue;
                        using (RegistryKey key = baseKey.OpenSubKey(sub, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
                        {
                            if (key == null) continue;
                            RegistrySecurity sec = key.GetAccessControl(AccessControlSections.Access);
                            sec.RemoveAccessRuleAll(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                                RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Deny));
                            key.SetAccessControl(sec);
                        }
                    }
                }
                catch (Exception ex) { Log.Warn("还原注册表权限失败：" + ex.Message); }
            }
        }
    }
}
