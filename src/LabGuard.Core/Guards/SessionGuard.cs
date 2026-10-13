using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LabGuard.Core.Config;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// **防会话逃逸**（v0.13，2026-10-13）：
    ///   ① 禁止快速用户切换 —— `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\HideFastUserSwitching=1`
    ///      （登录界面 / 开始菜单 / 任务管理器里的「切换用户」入口都没了）；
    ///   ② 禁止远程桌面接入 —— `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\fDenyTSConnections=1`
    ///      以及策略位 `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\fDenyTSConnections=1`
    ///      （防止学生用 RDP 再开一个会话逃出当前会话的管控）。
    ///
    /// 两条都写**机器级**位置：不管学生登录的是哪个账号，策略都在——这正是"换用户逃逸"的死穴。
    ///
    /// **落盘前先备份原值**（`session-policy-backup.json`），退出/卸载时**逐项还原**（原本没有的值就删掉）——
    /// 这条纪律来自 SAFEMODE 那次的教训：程序动过的系统开关，程序必须能还回去，
    /// 否则教师机侧"停监控/卸载"之后会留下一台"还在拒绝 RDP 的怪机器"。
    ///
    /// 诚实边界见 <see cref="SessionSettings"/> 的注释（可以注销、知道别的账号密码仍可登录、本地管理员/PE 挡不住）。
    /// </summary>
    public sealed class SessionGuard : GuardBase
    {
        public override string Name => "防会话逃逸";
        public override bool Enabled => Context?.Config.Session.Enabled ?? false;
        protected override int IntervalMs => 30000;

        private const string PoliciesSystem = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string TerminalServer = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        private const string TsPolicies = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        private const string FastSwitchValue = "HideFastUserSwitching";
        private const string DenyRdpValue = "fDenyTSConnections";

        private string BackupPath => Path.Combine(ConfigStore.DataDir, "session-policy-backup.json");

        protected override void OnStart()
        {
            Backup();
            Apply();
        }

        protected override void OnTick()
        {
            var s = Context.Config.Session;
            bool ok = true;
            if (s.BlockFastUserSwitch)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.LocalMachine, PoliciesSystem, FastSwitchValue) ?? 0) == 1;
            if (s.DenyRemoteDesktop)
                ok &= Convert.ToInt32(SystemActions.GetRegistryValue(RegistryHive.LocalMachine, TerminalServer, DenyRdpValue) ?? 0) == 1;
            if (!ok)
            {
                Context.Report(Name, "检测到防会话逃逸策略被改回，已自动还原。", ViolationAction.Notify);
                Apply();
            }
            SetStatus(ok ? "换用户/新会话已封堵" : "已恢复被改动的策略");
        }

        private void Apply()
        {
            var s = Context.Config.Session;
            if (!s.Enabled) return;
            if (s.BlockFastUserSwitch)
                SystemActions.SetRegistryValue(RegistryHive.LocalMachine, PoliciesSystem, FastSwitchValue, 1, RegistryValueKind.DWord);
            if (s.DenyRemoteDesktop)
            {
                SystemActions.SetRegistryValue(RegistryHive.LocalMachine, TerminalServer, DenyRdpValue, 1, RegistryValueKind.DWord);
                SystemActions.SetRegistryValue(RegistryHive.LocalMachine, TsPolicies, DenyRdpValue, 1, RegistryValueKind.DWord);
            }
            Log.Info("已应用防会话逃逸策略（" +
                     (s.BlockFastUserSwitch ? "禁快速切换用户" : "") +
                     (s.DenyRemoteDesktop ? (s.BlockFastUserSwitch ? " + " : "") + "禁远程桌面接入" : "") + "）");
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            Dictionary<string, string> backup = LoadBackup();
            if (backup.Count == 0)
            {
                // **没有备份 = 我们从来没写过这些值**（备份是 OnStart 的第一步）。
                // 这时候绝不能"顺手删掉"——那会把别人（管理员/组策略）设的值一起抹掉。
                Log.Info("没有防会话逃逸的原值备份 → 本机这两项不是本程序写的，停止时**不动系统**");
                return;
            }
            Restore(backup, PoliciesSystem, FastSwitchValue);
            Restore(backup, TerminalServer, DenyRdpValue);
            Restore(backup, TsPolicies, DenyRdpValue);
            try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
            Log.Info("已还原防会话逃逸策略（换用户/远程桌面回到装机前的状态）");
        }

        // ------------------------------------------------------------------ 原值备份 / 还原

        private static string Key(string subKey, string name) { return subKey + "|" + name; }

        /// <summary>
        /// 备份"我们即将改写的这几个值"的原始状态：原本有值就记下来，原本没有就记成空串（= 还原时删除）。
        /// 只在**第一次**启动时备份（已存在备份文件就不覆盖），否则第二次启动会把"我们自己写进去的值"
        /// 当成原值备份下来，退出时还原不回去——这是个很容易埋的坑。
        /// </summary>
        private void Backup()
        {
            try
            {
                // 干跑模式（自检 / 沙箱测试台）**不落任何痕迹**：既然没改系统，就不需要备份，
                // 更不该往真实 %ProgramData% 里写文件（自检纪律：不碰真实 ProgramData）。
                if (Log.DryRun)
                {
                    Log.Info("[dry] 不写原值备份（干跑模式不改系统，停止时也无需还原）");
                    return;
                }
                if (File.Exists(BackupPath)) return;
                var doc = new Dictionary<string, object>();
                foreach (var kv in new[] {
                    new KeyValuePair<string, string>(PoliciesSystem, FastSwitchValue),
                    new KeyValuePair<string, string>(TerminalServer, DenyRdpValue),
                    new KeyValuePair<string, string>(TsPolicies, DenyRdpValue) })
                {
                    object cur = SystemActions.GetRegistryValue(RegistryHive.LocalMachine, kv.Key, kv.Value);
                    doc[Key(kv.Key, kv.Value)] = cur == null ? "" : Convert.ToString(cur,
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                Directory.CreateDirectory(ConfigStore.DataDir);
                File.WriteAllText(BackupPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(doc),
                    new UTF8Encoding(false));
                Log.Info("已备份防会话逃逸相关的系统开关原值：" + BackupPath);
            }
            catch (Exception ex)
            {
                Log.Warn("备份防会话逃逸原值失败（退出时可能无法完全还原）：" + ex.Message);
            }
        }

        private Dictionary<string, string> LoadBackup()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(BackupPath)) return map;
                var doc = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(BackupPath, Encoding.UTF8));
                if (doc == null) return map;
                foreach (var kv in doc) map[kv.Key] = kv.Value as string;
            }
            catch (Exception ex)
            {
                Log.Warn("读取防会话逃逸备份失败（按『原本没有值』还原）：" + ex.Message);
            }
            return map;
        }

        /// <summary>有备份且原先有值 → 写回原值；原先没有值 → 删除（回到系统默认）。调用方保证"有备份"。</summary>
        private static void Restore(Dictionary<string, string> backup, string subKey, string name)
        {
            try
            {
                string raw;
                bool known = backup.TryGetValue(Key(subKey, name), out raw);
                if (known && !string.IsNullOrEmpty(raw))
                {
                    int v;
                    if (int.TryParse(raw, out v))
                        SystemActions.SetRegistryValue(RegistryHive.LocalMachine, subKey, name, v, RegistryValueKind.DWord);
                    else
                        SystemActions.SetRegistryValue(RegistryHive.LocalMachine, subKey, name, raw, RegistryValueKind.String);
                    return;
                }
                SystemActions.DeleteRegistryValue(RegistryHive.LocalMachine, subKey, name);
            }
            catch (Exception ex)
            {
                Log.Warn("还原注册表值失败（" + subKey + "\\" + name + "）：" + ex.Message);
            }
        }
    }
}
