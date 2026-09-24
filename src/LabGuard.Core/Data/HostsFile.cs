using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Data
{
    /// <summary>
    /// hosts 黑名单管理：生成"受管区块"、备份原文件、以 127.0.0.1 屏蔽域名、刷新 DNS。
    /// 只维护自己的区块（BEGIN/END 标记之间的内容），不影响其它行。
    /// </summary>
    public static class HostsFile
    {
        public const string BeginMark = "# ==== LabGuard 黑名单开始（由本程序自动生成，请勿手工修改） ====";
        public const string EndMark = "# ==== LabGuard 黑名单结束 ====";

        public static string HostsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        public static string BackupPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts.labguard-bak");

        public static void Backup()
        {
            try
            {
                if (File.Exists(HostsPath) && !File.Exists(BackupPath))
                {
                    File.Copy(HostsPath, BackupPath, false);
                    Log.Info("已备份 hosts -> " + BackupPath);
                }
            }
            catch (Exception ex) { Log.Warn("备份 hosts 失败：" + ex.Message); }
        }

        public static bool HasManagedBlock()
        {
            try
            {
                return File.Exists(HostsPath) &&
                       File.ReadAllText(HostsPath, Encoding.UTF8).Contains(BeginMark);
            }
            catch { return false; }
        }

        public static IReadOnlyList<string> ReadDomains()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(HostsPath)) return list;
                bool inBlock = false;
                foreach (string raw in File.ReadAllLines(HostsPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line == BeginMark) { inBlock = true; continue; }
                    if (line == EndMark) { inBlock = false; continue; }
                    if (!inBlock || line.Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) list.Add(parts[1]);
                }
            }
            catch (Exception ex) { Log.Warn("读取 hosts 失败：" + ex.Message); }
            return list;
        }

        /// <summary>把黑名单写入 hosts 的受管区块。</summary>
        public static bool Apply(IEnumerable<string> domains, string sinkAddress)
        {
            try
            {
                string blockText = BuildBlock(domains, sinkAddress);
                string current = File.Exists(HostsPath) ? File.ReadAllText(HostsPath, Encoding.UTF8) : "";
                string updated = ReplaceBlock(current, blockText);
                if (updated == current) return false;

                if (Log.DryRun)
                {
                    Log.Info("[dry] 将写入 hosts 受管区块（" + blockText.Length + " 字节）");
                    return true;
                }

                Backup();
                File.WriteAllText(HostsPath, updated, new UTF8Encoding(false));
                Log.Info("已更新 hosts 黑名单（" + ReadDomains().Count + " 条）");
                FlushDns();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写入 hosts 失败", ex);
                return false;
            }
        }

        public static void Cleanup()
        {
            try
            {
                if (!File.Exists(HostsPath)) return;
                string current = File.ReadAllText(HostsPath, Encoding.UTF8);
                string updated = ReplaceBlock(current, "");
                if (Log.DryRun) { Log.Info("[dry] 将清理 hosts 受管区块"); return; }
                if (updated != current) File.WriteAllText(HostsPath, updated, new UTF8Encoding(false));
                Log.Info("已清理 hosts 受管区块");
                FlushDns();
            }
            catch (Exception ex) { Log.Warn("清理 hosts 失败：" + ex.Message); }
        }

        public static void FlushDns()
        {
            SystemActions.Run("ipconfig.exe", "/flushdns");
        }

        /// <summary>读取 hosts 原文（不做任何解释）。</summary>
        public static string ReadRaw()
        {
            try { return File.Exists(HostsPath) ? File.ReadAllText(HostsPath, Encoding.UTF8) : null; }
            catch (Exception ex) { Log.Warn("读取 hosts 原文失败：" + ex.Message); return null; }
        }

        /// <summary>写回 hosts 原文（用于把被学生改过的内容还原到基线）。</summary>
        public static bool WriteRaw(string content)
        {
            if (content == null) return false;
            if (Log.DryRun) { Log.Info("[dry] 将还原 hosts 原文（" + content.Length + " 字符）"); return true; }
            try
            {
                File.WriteAllText(HostsPath, content, new UTF8Encoding(false));
                Log.Warn("已把 hosts 还原到基线内容");
                return true;
            }
            catch (Exception ex) { Log.Warn("还原 hosts 失败：" + ex.Message); return false; }
        }

        public static string Sha256Of(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ ACL 保护（防学生本地写映射绕过集中 DNS）
        /// <summary>
        /// 把 hosts 文件设为"管理员/SYSTEM 可写、普通用户只读"。
        /// 注意用"收紧允许"而不是"显式拒绝"，否则连管理员也改不了（Deny 对所有人生效）。
        /// </summary>
        public static void ProtectWithAcl(bool protect)
        {
            try
            {
                if (!File.Exists(HostsPath)) return;
                if (Log.DryRun)
                {
                    Log.Info("[dry] 将" + (protect ? "收紧" : "放开") + " hosts 文件权限：" + HostsPath);
                    return;
                }
                var info = new FileInfo(HostsPath);
                System.Security.AccessControl.FileSecurity sec = info.GetAccessControl();
                if (protect)
                {
                    sec.SetAccessRuleProtection(true, false);
                    const System.Security.AccessControl.InheritanceFlags none = System.Security.AccessControl.InheritanceFlags.None;
                    const System.Security.AccessControl.PropagationFlags prop = System.Security.AccessControl.PropagationFlags.None;
                    sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                        System.Security.AccessControl.FileSystemRights.FullControl, none, prop, System.Security.AccessControl.AccessControlType.Allow));
                    sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                        System.Security.AccessControl.FileSystemRights.FullControl, none, prop, System.Security.AccessControl.AccessControlType.Allow));
                    sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                        System.Security.AccessControl.FileSystemRights.Read, none, prop, System.Security.AccessControl.AccessControlType.Allow));
                }
                else
                {
                    sec.SetAccessRuleProtection(false, true); // 恢复继承
                }
                info.SetAccessControl(sec);
                Log.Info("hosts 文件权限已" + (protect ? "收紧为只读（普通用户）" : "恢复默认继承"));
            }
            catch (Exception ex)
            {
                Log.Warn("设置 hosts 文件权限失败：" + ex.Message);
            }
        }

        public static bool IsAclProtected()
        {
            try
            {
                if (!File.Exists(HostsPath)) return false;
                var info = new FileInfo(HostsPath);
                System.Security.AccessControl.FileSecurity sec = info.GetAccessControl();
                if (sec.AreAccessRulesProtected == false) return false;
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in
                         sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                    var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
                    if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid) &&
                        (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.Write) == System.Security.AccessControl.FileSystemRights.Write)
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>生成受管区块文本（公开以便自检）。</summary>
        public static string BuildBlock(IEnumerable<string> domains, string sinkAddress)
        {
            var block = new StringBuilder();
            block.AppendLine(BeginMark);
            block.AppendLine("# 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (string d in domains)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                block.AppendLine(sinkAddress + " " + d.Trim());
            }
            block.AppendLine(EndMark);
            return block.ToString();
        }

        /// <summary>把受管区块替换进 hosts 原文（公开以便自检）。</summary>
        public static string ReplaceBlock(string content, string newBlock)
        {
            string[] lines = (content ?? "").Replace("\r\n", "\n").Split('\n');
            var result = new List<string>();
            bool inBlock = false;
            foreach (string line in lines)
            {
                if (line.Trim() == BeginMark) { inBlock = true; continue; }
                if (line.Trim() == EndMark) { inBlock = false; continue; }
                if (inBlock) continue;
                result.Add(line);
            }
            string body = string.Join("\r\n", result).TrimEnd();
            if (newBlock.Length == 0) return body + "\r\n";
            return body + "\r\n\r\n" + newBlock.Replace("\r\n", "\r\n");
        }
    }
}
