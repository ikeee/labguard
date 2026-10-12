using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Server
{
    /// <summary>
    /// 最后一份**验签通过**的服务器策略（信封原文）落盘缓存。
    ///
    /// 为什么不需要额外签名：信封本身就是服务器签名的——缓存被改，验签就过不了；
    /// 缓存被换成一份更旧的合法信封也不行（<see cref="ServerClient"/> 的 seq 单调检查拦住）。
    /// 目录/文件再叠一层 ACL（管理员/SYSTEM 可写、其他用户只读），
    /// 标准学生连"改缓存"这个动作都做不了。
    ///
    /// 用途：① 重启后不必等服务器就能继续按最后生效策略管控（机房不失控）；
    /// ② 提供 seq 基线，防止把旧策略当新策略应用（回滚/重放）。
    /// </summary>
    public static class ServerPolicyCache
    {
        /// <summary>缓存目录。默认 <c>%ProgramData%\LabGuard</c>；自检注入临时目录用。</summary>
        public static string DataDir { get; set; } = ConfigStore.DataDir;

        public static string CachePath { get { return System.IO.Path.Combine(DataDir, "server-policy.cache"); } }

        /// <summary>本进程内最近一次"已应用/已加载"的 seq（防回滚基线）。</summary>
        public static ulong LastSeq { get; private set; }

        /// <summary>写入缓存（信封原文）。失败只告警：内存里的现行策略不受影响。</summary>
        public static bool TrySave(string envelope)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(CachePath, envelope ?? "", new UTF8Encoding(false));
                Harden(CachePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("写服务器策略缓存失败（不影响本次生效）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 读取并**验签**缓存。任何失败（缺失 / 签名不对 / 公钥换了）→ 返回 false 并清空基线，
        /// 调用方按"没有缓存"处理（继续单机管控，等服务器下发）。
        /// </summary>
        public static bool TryLoad(RSA publicKey, out EnvelopeData envelope)
        {
            envelope = null;
            try
            {
                if (!File.Exists(CachePath)) { LastSeq = 0; return false; }
                string text = File.ReadAllText(CachePath, Encoding.UTF8);
                EnvelopeData env = PolicyEnvelope.Parse(text, publicKey);
                if (!env.Verified)
                {
                    Log.Error("服务器策略缓存验签失败，已忽略（疑似被篡改）：" + env.Error);
                    LastSeq = 0;
                    return false;
                }
                LastSeq = env.Seq;
                envelope = env;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("读取服务器策略缓存失败，按无缓存处理", ex);
                LastSeq = 0;
                return false;
            }
        }

        /// <summary>应用成功后推进基线（供 ServerClient 调用）。</summary>
        public static void MarkApplied(ulong seq)
        {
            if (seq > LastSeq) LastSeq = seq;
        }

        /// <summary>清缓存（解除配对 / 关掉服务器管控时用）。</summary>
        public static void Clear()
        {
            try { if (File.Exists(CachePath)) File.Delete(CachePath); }
            catch (Exception ex) { Log.Warn("清除服务器策略缓存失败：" + ex.Message); }
            LastSeq = 0;
        }

        /// <summary>ACL 加固：改缓存 = 改管控策略，只允许管理员/SYSTEM 写。</summary>
        private static void Harden(string path)
        {
            try
            {
                var info = new FileInfo(path);
                FileSecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    FileSystemRights.Read | FileSystemRights.ExecuteFile,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                Log.Warn("加固策略缓存权限失败：" + ex.Message);
            }
        }
    }
}
