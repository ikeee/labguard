using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Core.Server
{
    /// <summary>
    /// 一次信封解析的结果。<see cref="Verified"/> 为 false 时其余字段一律不可信。
    /// </summary>
    public sealed class EnvelopeData
    {
        public ulong Seq;
        public DateTime IssuedAtUtc;
        public bool Paused;
        public string PolicyJson;
        public string SignatureBase64;
        public bool Verified;
        /// <summary>拒绝原因（写日志/界面提示用）。Verified = true 时为 null。</summary>
        public string Error;
    }

    /// <summary>
    /// LGSRV1 策略信封（教师机服务端下发）：
    /// <c>LGSRV1|seq|issuedAt|paused|policyJson|signature</c>，
    /// 签名 = RSA-SHA256(PKCS#1 v1.5)，覆盖前五段拼接的 UTF-8 字节。
    ///
    /// 本文件与教师机仓库 <c>labguard-server</c> 的 <c>Envelope/PolicyEnvelope.cs</c>
    /// **逐行同源**（协议真相源 = <c>docs/protocol-v1.md</c>，见该文件的"变更纪律"）：
    /// 客户端只做"解析 + 验签"，<see cref="Build"/> 仅供自检构造夹具与双仓协议一致性核对。
    /// </summary>
    public static class PolicyEnvelope
    {
        public const string Magic = "LGSRV1";

        /// <summary>组装并对给定 RSA 私钥签名（教师机侧使用；客户端只用于自检夹具）。</summary>
        public static string Build(ulong seq, DateTime issuedAtUtc, bool paused, string policyJson, RSA rsa)
        {
            if (policyJson == null) throw new ArgumentNullException(nameof(policyJson));
            if (rsa == null) throw new ArgumentNullException(nameof(rsa));
            string payload = SignedPayload(seq, issuedAtUtc, paused, policyJson);
            byte[] sig = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return payload + "|" + Convert.ToBase64String(sig);
        }

        public static string SignedPayload(ulong seq, DateTime issuedAtUtc, bool paused, string policyJson)
        {
            return Magic + "|" + seq.ToString(CultureInfo.InvariantCulture) + "|" +
                   issuedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "|" +
                   (paused ? "1" : "0") + "|" + policyJson;
        }

        /// <summary>
        /// 解析并验签。任何结构性问题 / 验签失败都返回 <c>Verified=false</c>（不抛异常，
        /// 调用方按 <see cref="EnvelopeData.Error"/> 决定告警口径）。
        ///
        /// 解析约定（与教师机同源）：前 4 个 '|' 划定头部，最后一个 '|' 分隔签名
        /// （base64 不含 '|'）——policyJson 里允许出现任意 '|'（List 字段的提示文案就含竖线，不能 naive Split）。
        /// </summary>
        public static EnvelopeData Parse(string envelope, RSA rsa)
        {
            var d = new EnvelopeData { Verified = false };
            if (string.IsNullOrEmpty(envelope)) { d.Error = "信封为空"; return d; }
            if (rsa == null) { d.Error = "尚未配对（没有可用的服务器公钥）"; return d; }

            int p1 = envelope.IndexOf('|');
            if (p1 < 0) { d.Error = "信封结构不完整（没有分隔符）"; return d; }
            string magic = envelope.Substring(0, p1);
            if (magic != Magic)
            {
                d.Error = "协议魔数不认识（需要 " + Magic + "，实际 " + magic + "）——拒绝，保持现有策略";
                return d;
            }
            int p2 = envelope.IndexOf('|', p1 + 1);
            int p3 = p2 < 0 ? -1 : envelope.IndexOf('|', p2 + 1);
            int p4 = p3 < 0 ? -1 : envelope.IndexOf('|', p3 + 1);
            if (p4 < 0) { d.Error = "信封结构不完整（缺少 seq/issuedAt/paused/policy 段）"; return d; }
            int pSig = envelope.LastIndexOf('|');
            if (pSig <= p4) { d.Error = "信封结构不完整（缺少签名段）"; return d; }

            if (!ulong.TryParse(envelope.Substring(p1 + 1, p2 - p1 - 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out d.Seq))
            { d.Error = "seq 不是合法的非负整数"; return d; }

            if (!DateTime.TryParse(envelope.Substring(p2 + 1, p3 - p2 - 1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out d.IssuedAtUtc))
            { d.Error = "issuedAt 不是合法的 ISO-8601 时间"; return d; }

            string pausedField = envelope.Substring(p3 + 1, p4 - p3 - 1);
            if (pausedField != "0" && pausedField != "1")
            { d.Error = "paused 字段只能是 0 或 1"; return d; }
            d.Paused = pausedField == "1";

            d.PolicyJson = envelope.Substring(p4 + 1, pSig - p4 - 1);
            d.SignatureBase64 = envelope.Substring(pSig + 1);

            byte[] sig;
            try { sig = Convert.FromBase64String(d.SignatureBase64); }
            catch { d.Error = "签名不是合法 base64"; return d; }

            string payload = envelope.Substring(0, pSig);
            try
            {
                d.Verified = rsa.VerifyData(Encoding.UTF8.GetBytes(payload), sig,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                d.Error = "验签异常：" + ex.Message;
                d.Verified = false;
                return d;
            }
            if (!d.Verified) d.Error = "签名验证失败（伪服务器 / 信封被篡改 / 公钥不匹配）";
            return d;
        }

        /// <summary>防回滚/防重放规则（纯函数，双仓同款语义）：新信封的 seq 必须严格更大。</summary>
        public static bool IsNewer(ulong candidateSeq, ulong appliedSeq)
        {
            return candidateSeq > appliedSeq;
        }
    }
}
