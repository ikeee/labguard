using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Server
{
    /// <summary>配对文件内容（labguard-pairing/1）。教师机服务端「导出配对文件」产出，U 盘/共享拷到学生机导入。</summary>
    public sealed class PairingInfo
    {
        public string Format;
        public string ServerHost;
        public string[] Addresses;
        public int Port;
        public string PublicKeyXml;
        public string Fingerprint;
        public string GeneratedAt;
        /// <summary>配对文件原文（原样留档：它是"可人工核对的凭据"，不重写成自己解析出来的样子）。</summary>
        public string RawJson;

        public string Address { get { return Describe(); } }

        public string Describe()
        {
            string host = null;
            if (Addresses != null && Addresses.Length > 0) host = Addresses[0];
            if (string.IsNullOrEmpty(host)) host = ServerHost;
            if (string.IsNullOrEmpty(host)) host = "?";
            return host + ":" + Port;
        }
    }

    /// <summary>
    /// 服务器配对与本机公钥 pin。
    ///
    /// 信任根 = **本机落盘的服务器公钥**（<c>%ProgramData%\LabGuard\server-public.key</c>，
    /// ACL 只允许 管理员/SYSTEM 写、其他用户只读）。策略信封的 RSA 签名用这把公钥验，
    /// 所以「伪服务器 / 改策略 / 重放旧策略」都过不了验签这一关。
    ///
    /// 配对文件本身是明文（只含公钥、主机名、IP、端口、指纹），泄露无害；
    /// 但它**必须防篡改**——替换公钥 = 换掉信任根。故导入时强制核对指纹，
    /// 落盘后每次读取都重算指纹，不一致 → fail-closed（当作未配对，继续单机管控，只留日志）。
    /// </summary>
    public static class ServerPairing
    {
        public const string ExpectedFormat = "labguard-pairing/1";

        /// <summary>配对文件与公钥的落盘目录。默认 <c>%ProgramData%\LabGuard</c>；自检注入临时目录用（同 Log.Directory 的做法）。</summary>
        public static string DataDir { get; set; } = ConfigStore.DataDir;

        public static string PublicKeyPath { get { return Path.Combine(DataDir, "server-public.key"); } }
        public static string MetaPath { get { return Path.Combine(DataDir, "server-pairing.json"); } }

        /// <summary>是否已配对成功（= 公钥与本机记录都在、且指纹自洽、且能当 RSA 公钥用）。</summary>
        public static bool IsPaired { get { return LoadPinned() != null; } }

        /// <summary>解析配对文件文本。结构 / 格式 / 字段不合法一律抛 <see cref="InvalidDataException"/>（带中文原因）。</summary>
        public static PairingInfo Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("配对文件是空的");
            Dictionary<string, object> raw;
            try
            {
                raw = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("配对文件不是合法 JSON：" + ex.Message);
            }
            if (raw == null) throw new InvalidDataException("配对文件不是合法 JSON");

            var info = new PairingInfo { RawJson = json };
            info.Format = raw.TryGetValue("format", out object f) ? f as string : null;
            if (info.Format != ExpectedFormat)
                throw new InvalidDataException("配对文件格式不认识（期望 " + ExpectedFormat + "，实际 " +
                    (info.Format ?? "缺失") + "）——教师机服务端与客户端版本可能不匹配");

            info.ServerHost = raw.TryGetValue("serverHost", out object h) ? h as string : null;
            info.Port = raw.TryGetValue("port", out object p) && p is IConvertible ? Convert.ToInt32(p) : 0;
            if (info.Port <= 0 || info.Port > 65535)
                throw new InvalidDataException("配对文件端口非法：" + info.Port);

            // JavaScriptSerializer 把 JSON 数组映射成 ArrayList
            if (raw.TryGetValue("addresses", out object addrs) && addrs is System.Collections.IEnumerable en && !(addrs is string))
            {
                var list = new List<string>();
                foreach (object o in en) list.Add(o as string ?? "");
                info.Addresses = list.ToArray();
            }
            else info.Addresses = new string[0];

            info.PublicKeyXml = raw.TryGetValue("publicKeyXml", out object k) ? k as string : null;
            if (string.IsNullOrEmpty(info.PublicKeyXml) ||
                info.PublicKeyXml.IndexOf("<RSAKeyValue>", StringComparison.Ordinal) < 0)
                throw new InvalidDataException("配对文件里没有合法的 RSA 公钥");
            if (info.PublicKeyXml.IndexOf("<D>", StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("配对文件里是私钥（含 <D> 段）——绝不允许装到学生机上");

            info.Fingerprint = raw.TryGetValue("fingerprint", out object fp) ? fp as string : null;
            if (string.IsNullOrEmpty(info.Fingerprint))
                throw new InvalidDataException("配对文件缺少公钥指纹（无法人工核对）");

            info.GeneratedAt = raw.TryGetValue("generatedAt", out object g) ? g as string : null;
            return info;
        }

        /// <summary>
        /// 公钥指纹 = 公钥 XML（UTF-8 字节）的 SHA-256 前 8 字节 hex（16 个字符）。
        /// **与教师机仓库 RsaKeyStore.ComputeFingerprint 同算法**——两个仓库各自算出来的必须相等，
        /// 否则配对会被判为"指纹不符"。
        /// </summary>
        public static string ComputeFingerprint(string publicKeyXml)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(publicKeyXml ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 导入配对文件：核对指纹 → 校验公钥可用（RSA ≥ 2048）→ 落盘公钥 + 配对记录（ACL 加固）。
        /// 任一步失败都抛异常且**不落盘**（不能留下半配对的中间态）。
        /// </summary>
        public static PairingInfo Import(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("配对文件不存在：" + path, path);
            PairingInfo info = Parse(File.ReadAllText(path, Encoding.UTF8));

            string computed = ComputeFingerprint(info.PublicKeyXml);
            if (!string.Equals(computed, info.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("公钥指纹不符（文件里 " + info.Fingerprint +
                    "，实测 " + computed + "）——配对文件被篡改或损坏，拒绝导入");

            int keySize = ValidatePublicKey(info.PublicKeyXml);

            Directory.CreateDirectory(DataDir);
            File.WriteAllText(PublicKeyPath, info.PublicKeyXml, new UTF8Encoding(false));
            File.WriteAllText(MetaPath, info.RawJson, new UTF8Encoding(false));
            HardenFile(PublicKeyPath);
            HardenFile(MetaPath);
            Log.Info("已完成服务器配对：" + info.Describe() + "（指纹 " + computed + "，公钥 " + keySize + " 位）");
            return info;
        }

        /// <summary>读取本机 pin 住的配对记录；没配对 / 文件缺失 / 指纹不自洽 / 公钥不能用 → 返回 null（fail-closed）。</summary>
        public static PairingInfo LoadPinned()
        {
            try
            {
                if (!File.Exists(PublicKeyPath) || !File.Exists(MetaPath)) return null;
                PairingInfo meta = Parse(File.ReadAllText(MetaPath, Encoding.UTF8));
                string keyXml = File.ReadAllText(PublicKeyPath, Encoding.UTF8);
                string computed = ComputeFingerprint(keyXml);
                if (!string.Equals(computed, meta.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(keyXml.Trim(), (meta.PublicKeyXml ?? "").Trim(), StringComparison.Ordinal))
                {
                    Log.Error("本机服务器公钥与配对记录不一致（疑似被替换），已按未配对处理（继续单机管控）");
                    return null;
                }
                ValidatePublicKey(keyXml);
                meta.PublicKeyXml = keyXml;
                return meta;
            }
            catch (Exception ex)
            {
                Log.Error("读取配对记录失败，按未配对处理", ex);
                return null;
            }
        }

        /// <summary>取本机 pin 的服务器公钥；未配对或不可用返回 null。</summary>
        public static RSA LoadPublicKey()
        {
            PairingInfo info = LoadPinned();
            if (info == null) return null;
            try
            {
                var rsa = new RSACryptoServiceProvider();
                rsa.FromXmlString(info.PublicKeyXml);
                return rsa;
            }
            catch (Exception ex)
            {
                Log.Error("服务器公钥加载失败（按未配对处理）", ex);
                return null;
            }
        }

        /// <summary>解除配对（换教师机 / 退役机器用）。</summary>
        public static void Unpair()
        {
            foreach (string p in new[] { PublicKeyPath, MetaPath })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception ex) { Log.Warn("解除配对时删除失败：" + p + " :: " + ex.Message); }
            }
            Log.Info("已解除服务器配对");
        }

        /// <summary>公钥可用性校验（只认公钥；位长 &lt; 2048 拒绝）。返回位长。</summary>
        private static int ValidatePublicKey(string publicKeyXml)
        {
            var rsa = new RSACryptoServiceProvider();
            try
            {
                rsa.FromXmlString(publicKeyXml);
                if (rsa.KeySize < 2048)
                    throw new InvalidDataException("服务器公钥只有 " + rsa.KeySize + " 位（要求 ≥ 2048），拒绝使用");
                return rsa.KeySize;
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("服务器公钥无法解析：" + ex.Message);
            }
            finally { rsa.Dispose(); }
        }

        /// <summary>公钥/配对记录 ACL：管理员与 SYSTEM 完全控制，其他已认证用户只读（改公钥 = 换信任根，必须挡住）。</summary>
        private static void HardenFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                FileSecurity sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.None;
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    FileSystemRights.Read | FileSystemRights.ExecuteFile, inherit, PropagationFlags.None,
                    AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch (Exception ex)
            {
                // 加固失败不致命（可能非管理员运行）：告警，别静默
                Log.Warn("加固配对文件权限失败（" + path + "）：" + ex.Message);
            }
        }
    }
}
