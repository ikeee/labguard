using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// 服务器同步状态（跨进程可读）。
    ///
    /// 为什么要有它：真正跑轮询的是**服务**（SYSTEM 身份），而"当前由服务器管控 / 服务器要求全员暂停 /
    /// 最近同步时间"这些信息要被**别的进程**看到——设置面板（只读提示）、托盘（状态显示）。
    /// 所以状态既留在内存里（本进程即时读），也落到 `%ProgramData%\LabGuard\server-state.json`
    /// （其它进程 / 重启后读）。
    ///
    /// 权威性排序：**验签通过的信封 &gt; 状态文件 &gt; 默认值**。
    /// 也就是说"是否暂停"最终以能验签的信封为准（<see cref="InitFromCache"/>），
    /// 状态文件只是给人看的快照，被改了也换不来"解除管控"。
    /// </summary>
    public static class ServerSyncState
    {
        public static string DataDir { get; set; } = ConfigStore.DataDir;
        public static string StatePath { get { return Path.Combine(DataDir, "server-state.json"); } }

        private static readonly object Gate = new object();
        private static string _lastWritten;

        /// <summary>本机是否启用了服务器管控（由配置决定，配置一存就更新）。</summary>
        public static bool Enabled { get; private set; }
        /// <summary>服务器要求"全员暂停"（信封 paused=1）。</summary>
        public static bool Paused { get; private set; }
        public static ulong AppliedSeq { get; private set; }
        public static string Address { get; private set; }
        /// <summary>Polling / FallbackSingle / NotPaired / Disabled / Stopped（给人看）。</summary>
        public static string LinkState { get; private set; }
        public static DateTime LastSuccessUtc { get; private set; }
        public static string LastError { get; private set; }

        /// <summary>是否处于服务器暂停态（未启用服务器管控时一定为 false —— 防止关掉服务器后永远解不开暂停）。</summary>
        public static bool ServerPaused { get { return Enabled && Paused; } }

        /// <summary>从状态文件装载（进程启动时调用；文件不存在/损坏按默认值，不抛异常）。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var doc = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(StatePath, Encoding.UTF8));
                if (doc == null) return;
                int schema = doc.TryGetValue("schemaVersion", out object sc) && sc is IConvertible ? Convert.ToInt32(sc) : 0;
                if (schema != 1) { Log.Warn("服务器状态文件 schemaVersion 不认识（" + schema + "），按默认值处理"); return; }
                lock (Gate)
                {
                    Enabled = GetBool(doc, "enabled");
                    Paused = GetBool(doc, "paused");
                    AppliedSeq = GetUlong(doc, "appliedSeq");
                    Address = GetString(doc, "address");
                    LinkState = GetString(doc, "linkState");
                    LastError = GetString(doc, "lastError");
                    DateTime dt;
                    LastSuccessUtc = DateTime.TryParse(GetString(doc, "lastSuccessUtc"), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out dt) ? dt : DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读服务器状态文件失败，按默认值处理：" + ex.Message);
            }
        }

        /// <summary>
        /// 用**验签通过**的本地缓存初始化（比状态文件可信）：拿到 seq 与 paused。
        /// 服务启动时先调它，保证"服务器不在网、机器重启"后仍然维持最后的暂停/管控状态。
        /// </summary>
        public static void InitFromCache(RSA publicKey)
        {
            EnvelopeData env;
            if (!ServerPolicyCache.TryLoad(publicKey, out env)) return;
            lock (Gate)
            {
                AppliedSeq = env.Seq;
                Paused = env.Paused;
            }
            Save();
        }

        /// <summary>策略已应用（由宿主在 onApply 里调）。</summary>
        public static void MarkApplied(EnvelopeData env, string address)
        {
            lock (Gate)
            {
                Enabled = true;
                Paused = env.Paused;
                AppliedSeq = env.Seq;
                if (!string.IsNullOrEmpty(address)) Address = address;
                LinkState = ServerLinkState.Polling.ToString();
                LastSuccessUtc = DateTime.UtcNow;
                LastError = null;
            }
            Save();
        }

        /// <summary>更新连接状态（每次轮询后由宿主调；只在关键字段变化时落盘，避免每 5 秒写盘）。</summary>
        public static void MarkLink(ServerLinkState state, string address, string error, DateTime lastSuccessUtc)
        {
            lock (Gate)
            {
                LinkState = state.ToString();
                if (!string.IsNullOrEmpty(address)) Address = address;
                LastError = error;
                if (lastSuccessUtc > LastSuccessUtc) LastSuccessUtc = lastSuccessUtc;
            }
            Save();
        }

        /// <summary>
        /// 配置保存时同步"是否启用服务器管控"。**关键**：老师把服务器管控关掉时，
        /// 必须同时清掉暂停态——否则机器会停在"服务器暂停"里，而服务器已不再管它（永远解不开）。
        /// </summary>
        public static void NoteConfigSaved(GuardConfig cfg)
        {
            bool enabled = cfg != null && cfg.Server != null && cfg.Server.Enabled;
            lock (Gate)
            {
                Enabled = enabled;
                if (!enabled) { Paused = false; LinkState = ServerLinkState.Disabled.ToString(); }
            }
            Save();
        }

        /// <summary>解除配对 / 退役机器：清空状态（含暂停）。</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Enabled = false; Paused = false; AppliedSeq = 0; Address = null;
                LinkState = ServerLinkState.Disabled.ToString(); LastSuccessUtc = DateTime.MinValue; LastError = null;
            }
            Save();
        }

        /// <summary>给人看的一行摘要（托盘/面板用）。</summary>
        public static string Describe()
        {
            if (!Enabled) return "单机模式（未启用服务器管控）";
            string seq = AppliedSeq > 0 ? ("seq " + AppliedSeq) : "尚未收到策略";
            string link = string.IsNullOrEmpty(LinkState) ? "?" : LinkState;
            string when = LastSuccessUtc == DateTime.MinValue ? "尚未成功同步"
                : ("最近同步 " + LastSuccessUtc.ToLocalTime().ToString("HH:mm:ss"));
            string extra = Paused ? "· 服务器全员暂停中" : "";
            string err = string.IsNullOrEmpty(LastError) ? "" : ("· " + LastError);
            return link + " · " + seq + " · " + when + extra + err;
        }

        private static void Save()
        {
            try
            {
                string json;
                lock (Gate)
                {
                    var doc = new Dictionary<string, object>
                    {
                        { "schemaVersion", 1 },
                        { "enabled", Enabled },
                        { "paused", Paused },
                        { "appliedSeq", AppliedSeq },
                        { "address", Address ?? "" },
                        { "linkState", LinkState ?? "" },
                        { "lastSuccessUtc", LastSuccessUtc == DateTime.MinValue ? "" : LastSuccessUtc.ToString("o") },
                        { "lastError", LastError ?? "" },
                        { "updatedUtc", DateTime.UtcNow.ToString("o") },
                    };
                    json = new JavaScriptSerializer().Serialize(doc);
                    // 只有关键内容变化才落盘（每 5 秒轮询不该产生每 5 秒一次写盘）
                    if (json == _lastWritten) return;
                    _lastWritten = json;
                }
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(StatePath, json, new UTF8Encoding(false));
                Harden(StatePath);
            }
            catch (Exception ex)
            {
                Log.Warn("写服务器状态文件失败（只影响状态显示）：" + ex.Message);
            }
        }

        private static bool GetBool(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is bool b && b;
        }

        private static ulong GetUlong(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToUInt64(v, CultureInfo.InvariantCulture) : 0;
        }

        private static string GetString(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }

        /// <summary>ACL：状态文件是可展示信息，但"暂停开关"属于管控状态，别让标准用户改。</summary>
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
                Log.Warn("加固服务器状态文件权限失败：" + ex.Message);
            }
        }
    }
}
