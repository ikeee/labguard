using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Server
{
    /// <summary>与教师机服务器的连接状态（托盘 / 面板显示用）。</summary>
    public enum ServerLinkState
    {
        /// <summary>没有启用服务器管控（或已停止）：完全等同单机版行为。</summary>
        Disabled,
        /// <summary>启用了但没有配对（缺公钥 / 配对记录）：等同单机，直到完成配对。</summary>
        NotPaired,
        /// <summary>正常轮询中（服务器未就绪 / 单次网络抖动都算这里，不算"回退"）。</summary>
        Polling,
        /// <summary>连续失败达到阈值，已沿用最后生效策略（继续轮询，服务器一回来就回到 Polling）。</summary>
        FallbackSingle,
        Stopped
    }

    /// <summary>取策略的通道（真实实现 = HTTP；自检注入假实现，不必联网）。</summary>
    public interface IServerTransport
    {
        /// <summary>GET /api/version：返回响应体；服务端未就绪（503）返回 null；连接失败抛异常。</summary>
        string GetVersion(string address);
        /// <summary>GET /api/policy：返回信封原文。失败抛异常。</summary>
        string GetPolicy(string address);
        /// <summary>POST /api/checkin：心跳上报。失败抛异常。</summary>
        void Checkin(string address, string body);
    }

    /// <summary>
    /// 服务器策略同步客户端（学生机侧）。
    ///
    /// 状态机（协议 §4）：
    /// <code>
    /// [单机] --启用且已配对--&gt; [轮询中]
    /// [轮询中] --/api/version 的 seq 前进--&gt; 拉全文 → 验签 → seq 严格更大？
    ///     是 → 应用（老师本地暂停中则暂存 pending，恢复后应用）
    ///     否 → 拒绝 + 日志（伪服务器 / 篡改 / 回滚）
    /// [轮询中] --连续失败达阈值--&gt; [回退单机]（沿用最后生效策略，继续轮询）
    /// [回退单机] --任一次探测成功--&gt; [轮询中]
    /// </code>
    ///
    /// 纪律（踩过的坑）：轮询必须在**后台线程**，所有请求带超时，用**代际号**作废过期的在途结果；
    /// 绝不能把阻塞网络调用放到 UI 线程。
    /// </summary>
    public sealed class ServerClient : IDisposable
    {
        private readonly GuardConfig _cfg;
        private readonly PairingInfo _pairing;
        private readonly RSA _publicKey;
        private readonly IServerTransport _transport;
        private readonly Func<bool> _isLocalPaused;
        private readonly Action<EnvelopeData> _onApply;

        private Thread _thread;
        private volatile bool _running;
        private int _generation;

        private ulong _appliedSeq;
        private EnvelopeData _current;
        private PendingPolicy _pending;
        private int _failures;
        private DateTime _lastCheckinUtc = DateTime.MinValue;
        private bool _loggedNotReady;
        private bool _lastApplyFailed;          // 最近一次应用是否失败（上报给教师端做"应用失败"标注）
        private string _lastErrorCode;          // 稳定错误码（协议 §13.2 命名：LG-xxx-xxx）

        public ServerClient(GuardConfig cfg, PairingInfo pairing, RSA publicKey, IServerTransport transport,
            Func<bool> isLocalPaused, Action<EnvelopeData> onApply)
        {
            _cfg = cfg ?? new GuardConfig();
            _pairing = pairing;
            _publicKey = publicKey;
            _transport = transport ?? new HttpServerTransport();
            _isLocalPaused = isLocalPaused ?? (() => false);
            _onApply = onApply ?? (e => { });
            State = _cfg.Server.Enabled ? ServerLinkState.Stopped : ServerLinkState.Disabled;
        }

        public ServerLinkState State { get; private set; }
        public int ConsecutiveFailures { get { return _failures; } }
        public ulong AppliedSeq { get { return _appliedSeq; } }
        /// <summary>当前生效的服务器信封（null = 还没收到过）。</summary>
        public EnvelopeData Current { get { return _current; } }
        /// <summary>服务器要求"全员暂停"（信封 paused=1）且已生效。</summary>
        public bool ServerPaused { get { return _current != null && _current.Paused; } }
        /// <summary>因老师本地暂停而暂存、等待恢复后应用的策略。</summary>
        public bool HasPending { get { return _pending != null; } }
        public string LastError { get; private set; }
        public DateTime LastSuccessUtc { get; private set; }

        /// <summary>服务器地址：配置优先，留空则用配对文件里的（教师机 IP:端口）。</summary>
        public string Address
        {
            get
            {
                string a = _cfg.Server.Address;
                if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
                return _pairing != null ? _pairing.Describe() : null;
            }
        }

        /// <summary>装载缓存并启动轮询线程；返回 false = 当前不该跑（未启用 / 未配对）。</summary>
        public bool Start()
        {
            if (!_cfg.Server.Enabled) { State = ServerLinkState.Disabled; return false; }
            if (_pairing == null || _publicKey == null)
            {
                State = ServerLinkState.NotPaired;
                Log.Warn("已启用服务器管控，但本机尚未完成配对（缺服务器公钥）——继续单机管控");
                return false;
            }
            if (_thread != null && _running) return true;

            // 重启后先吃缓存：机房不失控（服务器不在也能沿用最后生效策略）
            EnvelopeData cached;
            if (ServerPolicyCache.TryLoad(_publicKey, out cached))
            {
                _current = cached;
                _appliedSeq = cached.Seq;
                Log.Info("已装载服务器策略缓存：seq=" + cached.Seq + (cached.Paused ? "（全员暂停）" : ""));
            }

            _running = true;
            _generation++;
            int gen = _generation;
            State = ServerLinkState.Polling;
            _thread = new Thread(() => Loop(gen)) { IsBackground = true, Name = "LabGuard.ServerPoll" };
            _thread.Start();
            Log.Info("服务器同步已启动：" + Address + "（轮询 " + _cfg.Server.PollSeconds + " 秒）");
            return true;
        }

        public void Stop()
        {
            _running = false;
            _generation++;                      // 作废在途结果
            Thread t = _thread;
            _thread = null;
            try { if (t != null && t.IsAlive) t.Join(2000); } catch { }
            if (State != ServerLinkState.Disabled) State = ServerLinkState.Stopped;
        }

        public void Dispose()
        {
            Stop();
            try { _publicKey?.Dispose(); } catch { }
        }

        // ------------------------------------------------------------------ 轮询

        private void Loop(int generation)
        {
            int pollMs = Math.Max(1, _cfg.Server.PollSeconds) * 1000;
            while (_running && generation == _generation)
            {
                try { Tick(DateTime.UtcNow); }
                catch (Exception ex) { Log.Error("服务器轮询异常（继续尝试）", ex); }
                for (int slept = 0; slept < pollMs && _running && generation == _generation; slept += 100)
                    Thread.Sleep(100);
            }
        }

        /// <summary>
        /// 一轮同步（自检直接驱动它，不必等真实线程）。顺序：
        /// ① 老师恢复后补应用 pending；② 探测并应用新策略；③ 到点上报心跳。
        /// </summary>
        public void Tick(DateTime nowUtc)
        {
            if (!_cfg.Server.Enabled) { State = ServerLinkState.Disabled; return; }
            if (_pairing == null || _publicKey == null) { State = ServerLinkState.NotPaired; return; }

            if (_pending != null && !_isLocalPaused())
            {
                PendingPolicy p = _pending;
                _pending = null;
                Apply(p.Envelope, p.Text, "老师已恢复，应用暂存的服务器策略");
            }

            try
            {
                string versionJson = _transport.GetVersion(Address);
                if (versionJson == null)
                {
                    // 服务端在，但还没配置过策略（503）：不算失败，等老师保存策略
                    if (!_loggedNotReady)
                    {
                        Log.Info("服务器已连通，但尚未配置策略（等待教师机保存并下发）");
                        _loggedNotReady = true;
                    }
                    _failures = 0;
                    State = ServerLinkState.Polling;
                    return;
                }
                _loggedNotReady = false;
                _failures = 0;
                LastSuccessUtc = nowUtc;
                State = ServerLinkState.Polling;

                ulong serverSeq = ParseSeq(versionJson);
                if (serverSeq <= _appliedSeq)
                {
                    CheckinIfDue(nowUtc);        // seq 单调：相等即"没变"，不必拉全文
                    return;
                }

                string policyText = _transport.GetPolicy(Address);
                EnvelopeData env = PolicyEnvelope.Parse(policyText, _publicKey);
                if (!env.Verified)
                {
                    LastError = env.Error;
                    Log.Error("服务器策略验签失败，已拒绝本次下发：" + env.Error);
                    return;
                }
                if (!PolicyEnvelope.IsNewer(env.Seq, _appliedSeq))
                {
                    LastError = "seq 未前进（" + env.Seq + " <= " + _appliedSeq + "）——拒绝回滚/重放";
                    Log.Error("服务器策略 seq 未前进（疑似回滚或重放），已拒绝：seq=" + env.Seq +
                              "，本机已应用 " + _appliedSeq);
                    return;
                }
                if (_isLocalPaused())
                {
                    _pending = new PendingPolicy(env, policyText);
                    Log.Info("老师本地暂停中：服务器策略 seq=" + env.Seq + " 已暂存，老师恢复后自动应用");
                    return;
                }
                Apply(env, policyText, "收到新策略");
                _lastCheckinUtc = DateTime.MinValue;   // 应用后立刻上报一次
                CheckinIfDue(nowUtc);
            }
            catch (Exception ex)
            {
                _failures++;
                LastError = ex.Message;
                Log.Warn("连接教师机失败（第 " + _failures + " 次）：" + ex.Message);
                if (_failures >= Math.Max(1, _cfg.Server.FailuresBeforeFallback))
                {
                    if (State != ServerLinkState.FallbackSingle)
                        Log.Warn("连续失败 " + _failures + " 次：回退单机模式（沿用最后生效策略继续管控，" +
                                 "教师机恢复后自动回到服务器管控）");
                    State = ServerLinkState.FallbackSingle;
                }
            }
        }

        private void Apply(EnvelopeData env, string text, string why)
        {
            try
            {
                _onApply(env);                       // 宿主：合并进本机配置 + 让引擎重新生效
                _current = env;
                _appliedSeq = env.Seq;
                ServerPolicyCache.MarkApplied(env.Seq);
                ServerPolicyCache.TrySave(text);
                LastError = null;
                _lastApplyFailed = false;
                _lastErrorCode = null;
                Log.Info(why + "：seq=" + env.Seq + "，paused=" + (env.Paused ? "1（全员暂停）" : "0"));
            }
            catch (Exception ex)
            {
                LastError = "应用失败：" + ex.Message;
                _lastApplyFailed = true;
                _lastErrorCode = "LG-POLICY-APPLY-FAILED";
                Log.Error("应用服务器策略失败（本机保持原有策略）", ex);
            }
        }

        private void CheckinIfDue(DateTime nowUtc)
        {
            int interval = Math.Max(10, _cfg.Server.CheckinSeconds);
            if ((nowUtc - _lastCheckinUtc).TotalSeconds < interval) return;
            try
            {
                _transport.Checkin(Address, BuildCheckinJson());
                _lastCheckinUtc = nowUtc;
            }
            catch (Exception ex)
            {
                // 心跳失败只记日志：策略下发才是要害
                Log.Warn("心跳上报失败：" + ex.Message);
                _lastCheckinUtc = nowUtc;
            }
        }

        /// <summary>checkin 请求体（协议 §3 字段表：machine 必填，version / seq / paused 可带）。</summary>
        public string BuildCheckinJson()
        {
            string machine = Environment.MachineName ?? "?";
            if (machine.Length > 256) machine = machine.Substring(0, 256);
            // 稳定身份 + 已应用序号 + 应用结果（协议 §3）：教师端据此区分"同一台"，并判断 已应用/待应用/应用失败
            string deviceId = Server.DeviceId.Get();
            string status = _lastApplyFailed ? "error" : "ok";
            string json = "{\"machine\":\"" + Escape(machine) + "\",\"version\":\"" + Escape(AppInfo.Version) +
                          "\",\"deviceId\":\"" + Escape(deviceId) + "\"" +
                          ",\"seq\":" + _appliedSeq.ToString(CultureInfo.InvariantCulture) +
                          ",\"appliedSeq\":" + _appliedSeq.ToString(CultureInfo.InvariantCulture) +
                          ",\"status\":\"" + status + "\"" +
                          ",\"paused\":" + (ServerPaused ? "true" : "false");
            if (_lastApplyFailed && !string.IsNullOrEmpty(_lastErrorCode))
                json += ",\"errorCode\":\"" + Escape(_lastErrorCode) + "\"";
            return json + "}";
        }

        private static ulong ParseSeq(string versionJson)
        {
            var dict = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                .Deserialize<Dictionary<string, object>>(versionJson);
            if (dict == null || !dict.TryGetValue("seq", out object v) || v == null)
                throw new InvalidDataException("/api/version 响应里没有 seq");
            return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private sealed class PendingPolicy
        {
            public readonly EnvelopeData Envelope;
            public readonly string Text;
            public PendingPolicy(EnvelopeData envelope, string text) { Envelope = envelope; Text = text; }
        }
    }

    /// <summary>
    /// 真实通道：机房局域网明文 HTTP（完整性由信封签名保证，不依赖传输层——协议 §1）。
    /// 纪律：3 秒超时（网卡 / 交换机抖动时不能挂住轮询线程）、不走系统代理（机房常配代理会直接连不上教师机）。
    /// </summary>
    public sealed class HttpServerTransport : IServerTransport
    {
        public int TimeoutMs { get; set; } = 3000;

        public string GetVersion(string address) { return Get(address, "/api/version"); }
        public string GetPolicy(string address) { return Get(address, "/api/policy"); }

        public void Checkin(string address, string body)
        {
            HttpWebRequest req = Build(address, "/api/checkin", "POST");
            req.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            req.ContentLength = bytes.Length;
            using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { }
        }

        private string Get(string address, string path)
        {
            HttpWebRequest req = Build(address, path, "GET");
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && (int)resp.StatusCode == 503) return null;   // 服务端未就绪（还没保存过策略）
                throw;
            }
        }

        private HttpWebRequest Build(string address, string path, string method)
        {
            string host = (address ?? "").Trim().TrimEnd('/');
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(7);
            var req = (HttpWebRequest)WebRequest.Create("http://" + host + path);
            req.Method = method;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.Proxy = null;                  // 机房常配代理：走系统代理会直接连不上教师机
            req.KeepAlive = false;
            req.UserAgent = AppInfo.ProductName + "/" + AppInfo.Version;
            return req;
        }
    }
}
