using System;
using System.Collections.Generic;
using System.Linq;
using LabGuard.Core.Data;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// hosts 黑名单守护（本地域名清单）：写入受管区块、隐藏文件、被删/被改即恢复。
    /// </summary>
    public sealed class HostsGuard : GuardBase
    {
        public override string Name => "hosts黑名单";
        /// <summary>写黑名单 或 保护 hosts 文件（只读 + 内容守候）任一开启就运行。</summary>
        public override bool Enabled => Context != null &&
                                        (Context.Config.Hosts.Enabled || Context.Config.Hosts.ProtectFileAcl);
        protected override int IntervalMs => 20000;

        private int _expected;
        private int _deletedReports;
        private int _restoreCount;
        private string _baselineContent;
        private string _baselineHash;

        protected override void OnStart()
        {
            if (Context.Config.Hosts.Enabled)
            {
                var domains = BuildDomains();
                _expected = domains.Count;
                HostsFile.Apply(domains, Context.Config.Hosts.SinkAddress);
                if (Context.Config.Hosts.HideHostsFile)
                {
                    SystemActions.SetFileAttributeHidden(HostsFile.HostsPath, true);
                    SystemActions.SetRegistryValue(Microsoft.Win32.RegistryHive.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2,
                        Microsoft.Win32.RegistryValueKind.DWord);
                    SystemActions.SetRegistryValue(Microsoft.Win32.RegistryHive.CurrentUser,
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", 0,
                        Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            // 即使用集中 DNS 过滤（不写黑名单），也要把 hosts 设成只读，
            // 并留一份内容基线（学生用管理员权限改 hosts 时能立刻还原）。
            if (Context.Config.Hosts.ProtectFileAcl)
            {
                HostsFile.ProtectWithAcl(true);
                _baselineContent = HostsFile.ReadRaw();
                _baselineHash = HostsFile.Sha256Of(_baselineContent);
                Log.Info("hosts 内容基线已记录（防学生本地写映射绕过集中 DNS）");
            }
        }

        protected override void OnTick()
        {
            if (!Context.Config.Hosts.Enabled)
            {
                if (!Context.Config.Hosts.ProtectFileAcl)
                {
                    SetStatus("未启用（集中 DNS 过滤）");
                    return;
                }
                bool changed = false;
                if (!HostsFile.IsAclProtected())
                {
                    HostsFile.ProtectWithAcl(true);
                    changed = true;
                }
                // 内容守候：学生（哪怕是管理员账号）往 hosts 里写映射 → 立刻还原
                string now = HostsFile.ReadRaw();
                if (now != null && _baselineHash != null && HostsFile.Sha256Of(now) != _baselineHash)
                {
                    _restoreCount++;
                    HostsFile.WriteRaw(_baselineContent);
                    HostsFile.FlushDns();
                    Context.Report(Name, "检测到 hosts 文件被修改，已还原（防止用本地映射绕过集中 DNS 过滤）。" +
                        (_restoreCount >= 3 ? " 多次被改，建议检查该学生账号权限。" : ""),
                        ViolationAction.Notify);
                    changed = true;
                }
                SetStatus(changed
                    ? "hosts 被改，已还原（累计 " + _restoreCount + " 次）"
                    : "未启用黑名单（hosts 只读 + 内容守候）");
                return;
            }

            if (!Context.Config.Hosts.Guard) { SetStatus("已写入（未开启守护）"); return; }

            if (!System.IO.File.Exists(HostsFile.HostsPath))
            {
                _deletedReports++;
                Context.Report(Name, _deletedReports > 1
                    ? "hosts 文件被反复删除，且没有可用备份。"
                    : "hosts 文件被删除，已自动恢复。", ViolationAction.Notify);
                HostsFile.Apply(BuildContextDomains(), Context.Config.Hosts.SinkAddress);
                SetStatus("hosts 被删除，已恢复");
                return;
            }

            if (!HostsFile.HasManagedBlock() || HostsFile.ReadDomains().Count != _expected)
            {
                Context.Report(Name, "hosts 黑名单被修改，已强制恢复。", ViolationAction.Notify);
                HostsFile.Apply(BuildContextDomains(), Context.Config.Hosts.SinkAddress);
                SetStatus("hosts 被修改，已恢复");
                return;
            }

            SystemActions.SetFileAttributeHidden(HostsFile.HostsPath, Context.Config.Hosts.HideHostsFile);
            SetStatus("生效中（" + _expected + " 个域名）");
        }

        private List<string> BuildDomains()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Context.Config.Hosts.UseBuiltInDomains)
            {
                foreach (string d in DefaultBlocklists.Domains) set.Add(d);
            }
            foreach (string d in Context.Config.Hosts.ExtraDomains)
            {
                foreach (string one in (d ?? "").Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    set.Add(one.Trim());
                }
            }
            return set.ToList();
        }

        private List<string> BuildContextDomains() => BuildDomains();

        protected override void OnStop()
        {
            if (Context != null && Context.Config.Hosts.Enabled)
            {
                HostsFile.Cleanup();
                SystemActions.SetFileAttributeHidden(HostsFile.HostsPath, false);
            }
            HostsFile.ProtectWithAcl(false);
        }
    }
}
