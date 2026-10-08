using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Config
{
    /// <summary>
    /// 配置与暂停旗标的完整性签名（红队 B3/B4 修复）。
    ///
    /// 为什么 DPAPI 不够还要加 HMAC：DPAPI LocalMachine 对"本机管理员"没有机密性
    /// （红队实测 20 行 Python 解开 config.json 并改写）。这里的 HMAC 不承诺"管理员改不了"，
    /// 它做两件事：
    ///   1) 抬高门槛——篡改不再是"解开→改→重新加密"三步，还得找到密钥、理解信封格式、重算签名；
    ///   2) 能检出——不动密钥的篡改/伪造在 Load/IsPaused 时被抓到：回退镜像 + 告警 + TamperSuspected。
    /// 对标准学生（机房推荐部署）这层是真机密：HKLM 密钥值他们连读都读不到。
    /// </summary>
    public static class ConfigIntegrity
    {
        private const string KeyValueName = "CfgKey";
        public const string EnvelopeMagic = "LGCFG2";
        public const string PauseMagic = "LGPAUSE1";
        private static byte[] _cachedKey;

        /// <summary>取签名密钥；不存在返回 null（= 升级前的老安装，调用方走一次性兼容分支）。</summary>
        public static byte[] TryGetKey()
        {
            if (_cachedKey != null) return _cachedKey;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ConfigStore.RegistryRoot))
                {
                    byte[] blob = k?.GetValue(KeyValueName) as byte[];
                    if (blob == null || blob.Length == 0) return null;
                    _cachedKey = ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine);
                    return _cachedKey;
                }
            }
            catch { return null; }
        }

        /// <summary>取密钥；没有就现场生成 32 字节随机密钥并落注册表（Save 路径用）。</summary>
        public static byte[] GetOrCreateKey()
        {
            byte[] key = TryGetKey();
            if (key != null) return key;
            try
            {
                key = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(key);
                byte[] blob = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ConfigStore.RegistryRoot))
                {
                    k?.SetValue(KeyValueName, blob, RegistryValueKind.Binary);
                }
                _cachedKey = key;
                return key;
            }
            catch (Exception ex)
            {
                Log.Warn("无法创建配置签名密钥（降级为不签名）：" + ex.Message);
                return null;
            }
        }

        public static string Sign(byte[] key, string payload)
        {
            using (var h = new HMACSHA256(key))
            {
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            }
        }

        public static bool Verify(byte[] key, string payload, string sig)
        {
            if (key == null || payload == null || sig == null) return false;
            string expect = Sign(key, payload);
            // 定长比较，避免时序侧信道（net48 没有 FixedTimeEquals）
            if (expect.Length != sig.Length) return false;
            int diff = 0;
            for (int i = 0; i < expect.Length; i++) diff |= expect[i] ^ sig[i];
            return diff == 0;
        }

        // ---------------------------------------------------------- 配置信封
        // 格式：LGCFG2\n<savedAtUtc.Ticks>\n<sig>\n<json>
        // sig = HMAC(key, "LGCFG2\n<ticks>\n" + json)

        public static string Wrap(byte[] key, string json, DateTime savedAtUtc)
        {
            string head = EnvelopeMagic + "\n" + savedAtUtc.Ticks + "\n";
            return head + Sign(key, head + json) + "\n" + json;
        }

        /// <summary>这段内容是不是签名信封（策略层用它区分"legacy 升级"与"签名待验"）。</summary>
        public static bool IsEnvelope(string content)
        {
            return content != null && content.StartsWith(EnvelopeMagic + "\n", StringComparison.Ordinal);
        }

        /// <summary>
        /// 解信封。**legacy（无信封）一律接受**——"接受后怎么处理"是策略层（ConfigStore）的事：
        /// 它要区分一次性升级（镜像也是 legacy）与单点回滚篡改（镜像已是签名新版）。
        /// 带信封的必须签名正确，否则返回 false（= 篡改）。
        /// </summary>
        public static bool TryUnwrap(byte[] key, string content, out string json, out DateTime savedAtUtc)
        {
            json = null;
            savedAtUtc = DateTime.MinValue;
            if (string.IsNullOrEmpty(content)) return false;
            if (!IsEnvelope(content))
            {
                json = content;                     // legacy：接受，策略层决定下一步
                return true;
            }
            if (key == null) return false;
            int l1 = content.IndexOf('\n');
            int l2 = content.IndexOf('\n', l1 + 1);
            int l3 = content.IndexOf('\n', l2 + 1);
            if (l1 < 0 || l2 < 0 || l3 < 0) return false;
            long ticks;
            if (!long.TryParse(content.Substring(l1 + 1, l2 - l1 - 1), out ticks)) return false;
            string head = content.Substring(0, l2 + 1);
            string sig = content.Substring(l2 + 1, l3 - l2 - 1);
            string body = content.Substring(l3 + 1);
            if (!Verify(key, head + body, sig)) return false;
            json = body;
            try { savedAtUtc = new DateTime(ticks, DateTimeKind.Utc); } catch { savedAtUtc = DateTime.MinValue; }
            return true;
        }

        // ---------------------------------------------------------- 暂停旗标
        // 格式：LGPAUSE1\n<createdAt.Ticks>\n<sig>
        // sig = HMAC(key, "LGPAUSE1\n<ticks>")

        public static string WrapPause(byte[] key, DateTime createdAt)
        {
            string head = PauseMagic + "\n" + createdAt.Ticks;
            return head + "\n" + Sign(key, head);
        }

        /// <summary>
        /// 解暂停旗标（红队 B3：伪造的"免死金牌"必须被认出）。
        /// 带 magic 的：密钥在且签名对才接受；不带 magic 的（legacy/学生随手写的）：
        /// 仅当本机还没有密钥（无法签名的降级态）才按 legacy 接受，否则一律判伪造。
        /// </summary>
        public static bool TryUnwrapPause(byte[] key, string content, out DateTime createdAt)
        {
            createdAt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(content)) return false;
            content = content.Trim();
            if (!content.StartsWith(PauseMagic + "\n", StringComparison.Ordinal))
            {
                if (key != null) return false;      // 有密钥的时代出现无签名旗标 = 伪造
                return DateTime.TryParse(content, out createdAt);   // 降级态：维持旧行为
            }
            if (key == null) return false;
            int l1 = content.IndexOf('\n');
            int l2 = content.IndexOf('\n', l1 + 1);
            if (l1 < 0 || l2 < 0) return false;
            string head = content.Substring(0, l2);   // "LGPAUSE1\n<ticks>"——与 WrapPause 的签名载荷一致
            long ticks;
            if (!long.TryParse(content.Substring(l1 + 1, l2 - l1 - 1), out ticks)) return false;
            string sig = content.Substring(l2 + 1);
            if (!Verify(key, head, sig)) return false;
            try { createdAt = new DateTime(ticks); } catch { return false; }
            return true;
        }

        /// <summary>
        /// B4c 修复：已安装的机器密码散列却是空的 = 配置被清过（篡改），
        /// 必须按"受损"处理；只有没装过的全新机器才允许"未配置"语义。
        /// </summary>
        public static bool IsSuspiciousEmptyPassword(bool isInstalled, string passwordHash)
        {
            return isInstalled && string.IsNullOrEmpty(passwordHash);
        }
    }
}
