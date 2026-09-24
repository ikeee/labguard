using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LabGuard.Core.Config;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 集中 DNS 锁定（推荐主防线，替代 hosts 黑名单）：
    /// 学生机 DNS 必须指向教师机的 AdGuard（或校内 DNS）。学生改成 8.8.8.8 / 114.114.114.114
    /// 就等于绕过全部域名过滤，所以这里每 15 秒核对一次网卡 DNS，被改就强制改回并记录。
    /// 另附带"教师机 AdGuard 是否还活着"的探针（UDP 53 查询是否有响应）。
    /// </summary>
    public sealed class DnsGuard : GuardBase
    {
        public override string Name => "DNS锁定";
        public override bool Enabled =>
            (Context?.Config.Network.Enabled ?? false) && (Context.Config.Network.EnforceDns);

        protected override int IntervalMs => 15000;

        private DateTime _lastProbe = DateTime.MinValue;
        private bool? _serverAlive;
        private int _fixedCount;

        protected override void OnStart()
        {
            var configured = Configured();
            if (configured.Count == 0)
            {
                SetStatus("未配置 DNS（跳过锁定）");
                return;
            }
            Log.Info("DNS 锁定目标：" + string.Join(", ", configured));
            Enforce(configured, false);
        }

        protected override void OnTick()
        {
            var configured = Configured();
            if (configured.Count == 0) { SetStatus("未配置 DNS（跳过锁定）"); return; }

            Enforce(configured, true);

            // 教师机 AdGuard 存活探针（每 60 秒一次）
            if ((DateTime.Now - _lastProbe).TotalSeconds >= 60)
            {
                _lastProbe = DateTime.Now;
                _serverAlive = Probe(configured[0], "www.bing.com", 1500);
                if (_serverAlive == false)
                {
                    if (HasUsableLocalIp())
                    {
                        // 本机网络是通的，只是集中 DNS 不响应 → 说明是教师机侧的问题，值得提醒
                        Log.Warn("DNS 服务器 " + configured[0] + " 无响应（教师机 AdGuard 未启动？）");
                        Context.Report(Name, "集中 DNS 服务器 " + configured[0] +
                            " 无响应：学生机现在无法解析域名（请检查教师机 AdGuard 是否在运行）。",
                            ViolationAction.Notify, "探针超时");
                    }
                    else
                    {
                        // 本机本身就是断网（拔网线/禁用网卡）→ 由 NetworkGuard 统一报"拔了网线"，
                        // 这里只记日志，避免同一次断网弹两条不同的提示
                        Log.Warn("DNS 探针失败：本机当前无可用 IP（已断网），不重复告警");
                    }
                }
            }

            string alive = _serverAlive == null ? "未探测" : (_serverAlive.Value ? "正常" : "无响应");
            SetStatus("DNS=" + string.Join(",", configured) + "（已还原 " + _fixedCount + " 次；服务器 " + alive + "）");
        }

        private List<string> Configured()
        {
            var list = new List<string>();
            foreach (string raw in Context.Config.Network.DnsServers ?? new List<string>())
            {
                foreach (string one in (raw ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ip = one.Trim();
                    if (LooksLikeIpv4(ip) && !list.Contains(ip)) list.Add(ip);
                }
            }
            return list;
        }

        private void Enforce(List<string> configured, bool report)
        {
            // 只锁"代表本机联网"的网卡：不动热点/Wi-Fi Direct 的 DNS，
            // 否则热点客户端（STEAM 课的机器人/平板）会被塞进教师机 DNS。
            HashSet<string> only = null;
            if (Context.Config.Network.DnsLockOnlyWatchedInterfaces)
            {
                string reason;
                only = new HashSet<string>(
                    NicRoles.SelectWatched(Context.Config, out reason).Select(n => n.Name),
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (NetworkInterface nic in SafeInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (only != null && !only.Contains(nic.Name)) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    if (props.UnicastAddresses.All(a => a.Address.AddressFamily != AddressFamily.InterNetwork)) continue;

                    var current = props.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();

                    if (!NeedsFix(current, configured)) continue;

                    _fixedCount++;
                    if (report)
                    {
                        Context.Report(Name, "检测到学生机 DNS 被修改（当前 " +
                            (current.Count == 0 ? "空" : string.Join(",", current)) +
                            "），已强制改回集中过滤服务器！", ViolationAction.Notify, nic.Name);
                    }
                    ApplyDns(nic.Name, configured);
                }
                catch (Exception ex)
                {
                    Log.Debug("检查 " + nic.Name + " 的 DNS 失败：" + ex.Message);
                }
            }
        }

        private static void ApplyDns(string nicName, List<string> servers)
        {
            SystemActions.Run("netsh.exe", "interface ip set dns name=\"" + nicName + "\" static " + servers[0] + " primary");
            for (int i = 1; i < servers.Count; i++)
            {
                SystemActions.Run("netsh.exe", "interface ip add dns name=\"" + nicName + "\" " + servers[i] + " index=" + (i + 1));
            }
            SystemActions.Run("ipconfig.exe", "/flushdns");
        }

        private static IEnumerable<NetworkInterface> SafeInterfaces()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return new NetworkInterface[0]; }
        }

        /// <summary>
        /// 本机是否还有可用 IPv4 —— 用于区分"本机断网"（只记日志）与"教师机 DNS 挂了"（要提示）。
        /// 已排除回环、169.254.*、0.0.0.0、以及移动热点网卡（192.168.137.1）。
        /// </summary>
        public static bool HasUsableLocalIp(GuardConfig config = null)
        {
            return Core.Interop.NicRoles.HasUsableLocalIp(config ?? new GuardConfig());
        }

        /// <summary>当前 DNS 是否与配置不一致（纯函数，便于自检）。</summary>
        public static bool NeedsFix(IEnumerable<string> current, IEnumerable<string> configured)
        {
            List<string> cur = Normalize(current);
            List<string> cfg = Normalize(configured);
            if (cfg.Count == 0) return false;
            if (cur.Count == 0) return true;
            for (int i = 0; i < cfg.Count; i++)
            {
                if (i >= cur.Count) return true;
                if (!string.Equals(cur[i], cfg[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>规范化 DNS 列表：去空、去重、保持顺序（纯函数）。</summary>
        public static List<string> Normalize(IEnumerable<string> servers)
        {
            var list = new List<string>();
            foreach (string s in servers ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string ip = s.Trim();
                if (!LooksLikeIpv4(ip)) continue;
                if (!list.Contains(ip)) list.Add(ip);
            }
            return list;
        }

        public static bool LooksLikeIpv4(string s)
        {
            IPAddress addr;
            return !string.IsNullOrWhiteSpace(s) && IPAddress.TryParse(s.Trim(), out addr) &&
                   addr.AddressFamily == AddressFamily.InterNetwork;
        }

        /// <summary>向指定 DNS 发一个 A 记录查询，收到任何应答即视为"服务器活着"。</summary>
        public static bool Probe(string dnsServer, string domain, int timeoutMs)
        {
            try
            {
                if (!LooksLikeIpv4(dnsServer)) return false;
                byte[] query = BuildQuery(domain, out ushort id);
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = timeoutMs;
                    udp.Client.SendTimeout = timeoutMs;
                    udp.Connect(dnsServer, 53);
                    udp.Send(query, query.Length);
                    IPEndPoint remote = null;
                    byte[] reply = udp.Receive(ref remote);
                    if (reply == null || reply.Length < 12) return false;
                    ushort replyId = (ushort)((reply[0] << 8) | reply[1]);
                    bool isResponse = (reply[2] & 0x80) != 0;
                    return replyId == id && isResponse;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("DNS 探针失败（" + dnsServer + "）：" + ex.Message);
                return false;
            }
        }

        private static byte[] BuildQuery(string domain, out ushort id)
        {
            id = (ushort)new Random().Next(1, 65000);
            var buffer = new List<byte>();
            buffer.Add((byte)(id >> 8));
            buffer.Add((byte)(id & 0xFF));
            buffer.Add(0x01); buffer.Add(0x00); // 标准查询
            buffer.Add(0x00); buffer.Add(0x01); // QDCOUNT = 1
            buffer.Add(0x00); buffer.Add(0x00);
            buffer.Add(0x00); buffer.Add(0x00);
            buffer.Add(0x00); buffer.Add(0x00);
            foreach (string label in (domain ?? "").Split('.'))
            {
                if (label.Length == 0) continue;
                buffer.Add((byte)label.Length);
                buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            buffer.Add(0x00);            // 域名结束
            buffer.Add(0x00); buffer.Add(0x01); // TYPE = A
            buffer.Add(0x00); buffer.Add(0x01); // CLASS = IN
            return buffer.ToArray();
        }

        protected override void OnStop() { }
    }
}
