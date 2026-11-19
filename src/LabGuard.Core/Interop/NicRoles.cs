using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 网卡角色判定：挑出"真正代表这台机器有没有网"的网卡，把热点/虚拟/蓝牙/Wi-Fi Direct 排除在外。
    ///
    /// 为什么必须有它：一体机机房"有线为主 + 无线常开做热点"——
    /// Windows 移动热点的网卡（固定 192.168.137.1，常显示为"本地连接* 10"）
    /// 会被误当作"网络正常"（遮罩不消失），也会被误当作"网络断了"（误弹遮罩）。
    /// </summary>
    public static class NicRoles
    {
        /// <summary>Windows 移动热点（ICS）主机侧固定网关，出现它基本可判定这是热点网卡。</summary>
        public const string IcsGatewayIp = "192.168.137.1";

        public static bool IsExcluded(string name, string description, IEnumerable<string> patterns)
        {
            string haystack = (name ?? "") + " " + (description ?? "");
            foreach (string p in patterns ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (haystack.IndexOf(p.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>是否"有线"网卡（用于『只监视有线』）。</summary>
        public static bool IsWiredType(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                    return true;
                default:
                    return false;
            }
        }

        public static IEnumerable<NetworkInterface> Usable()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return new NetworkInterface[0]; }
        }

        public static bool HasUsableIpv4(NetworkInterface nic, out string ip)
        {
            ip = null;
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
                if (nic.OperationalStatus != OperationalStatus.Up) return false;
                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = addr.Address.ToString();
                    if (s.StartsWith("169.254.") || s == "0.0.0.0") continue;
                    ip = s;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 一条可用于联网判据的完整链路：网卡 Up、有 IPv4，并且有默认网关。
        /// 固定 IP 机房也必须有网关；只有 IP、没有默认路由时不能算"有网"。
        /// </summary>
        public static bool IsOnline(NetworkInterface nic, out string ip, out string gateway)
        {
            ip = null;
            gateway = null;
            try
            {
                if (nic == null || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
                if (nic.OperationalStatus != OperationalStatus.Up) return false;

                IPInterfaceProperties props = nic.GetIPProperties();
                foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string candidate = addr.Address.ToString();
                    if (candidate == "0.0.0.0" || candidate.StartsWith("169.254.")) continue;
                    ip = candidate;
                    break;
                }
                if (string.IsNullOrEmpty(ip)) return false;

                foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string candidate = gw.Address.ToString();
                    if (candidate == "0.0.0.0") continue;
                    gateway = candidate;
                    break;
                }
                return !string.IsNullOrEmpty(gateway) && gateway != "0.0.0.0" && ip != IcsGatewayIp;
            }
            catch
            {
                return false;
            }
        }

        public static bool HasDefaultGateway(NetworkInterface nic)
        {
            string ip, gateway;
            return IsOnline(nic, out ip, out gateway);
        }

        /// <summary>
        /// 选出"代表本机联网状态"的网卡：
        /// 1) 设置里显式指定 WatchedInterfaces（按名字/描述匹配）优先；
        /// 2) 否则自动：排除热点/虚拟网卡后，优先取"有默认网关"的网卡；
        /// 3) 再退一步：取所有有可用 IPv4 的物理网卡。
        /// </summary>
        public static List<NetworkInterface> SelectWatched(GuardConfig config, out string reason)
        {
            var result = new List<NetworkInterface>();
            List<string> explicitNames = config?.Network?.WatchedInterfaces ?? new List<string>();
            List<string> excluded = config?.Network?.ExcludedInterfaces ?? new List<string>();

            var candidates = new List<NetworkInterface>();
            foreach (NetworkInterface nic in Usable())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (IsExcluded(nic.Name, nic.Description, excluded)) continue;
                    if ((config?.Network?.WatchWiredOnly ?? false) && !IsWiredType(nic.NetworkInterfaceType)) continue;
                    string ip;
                    if (HasUsableIpv4(nic, out ip) && ip == IcsGatewayIp)
                    {
                        Log.Debug("网卡 " + nic.Name + " 是移动热点网卡（" + ip + "），不作为联网判据");
                        continue;
                    }
                    candidates.Add(nic);
                }
                catch { }
            }

            // "只监视有线"但本机没有有线网卡 → 不能静默失去监控，退回自动选择并告警
            string fallbackNote = "";
            if (candidates.Count == 0 && (config?.Network?.WatchWiredOnly ?? false))
            {
                Log.Warn("设置里选了『只监视有线网卡』，但本机没找到有线网卡 → 暂时退回自动选择（请检查设置）");
                fallbackNote = "（只监视有线：本机没有有线网卡，已退回自动）";
                foreach (NetworkInterface nic in Usable())
                {
                    try
                    {
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                        if (IsExcluded(nic.Name, nic.Description, excluded)) continue;
                        string fallbackIp;
                        if (HasUsableIpv4(nic, out fallbackIp) && fallbackIp == IcsGatewayIp) continue;
                        candidates.Add(nic);
                    }
                    catch { }
                }
            }

            if (explicitNames.Count > 0)
            {
                foreach (NetworkInterface nic in candidates)
                {
                    foreach (string want in explicitNames)
                    {
                        if (string.IsNullOrWhiteSpace(want)) continue;
                        string w = want.Trim();
                        if (nic.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (nic.Description ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result.Add(nic);
                            break;
                        }
                    }
                }
                if (result.Count > 0)
                {
                    reason = "按设置的『只监控这些网卡』选中 " + result.Count + " 个";
                    return result;
                }
                Log.Warn("设置里指定的被监控网卡没找到，改为自动选择");
            }

            // 冷启动/开机前拔线：候选里必须保留物理网卡本身，即便它当前
            // Down、没有 IP、没有默认网关。把"候选"误做成"已联网网卡"会让
            // watchedTotal=0，NetworkGuard 永远无法判定断网。
            // 有线优先；无线只在没有有线网卡时兜底。
            foreach (NetworkInterface nic in candidates
                .OrderByDescending(n => IsWiredType(n.NetworkInterfaceType))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(nic);
            }
            reason = result.Count > 0
                ? "自动选中 " + result.Count + " 个物理网卡作为判据（有线优先；离线和在线都会保留）" + fallbackNote
                : "没有找到可判定的物理网卡（本机可能没有网卡）";
            return result;
        }

        /// <summary>本机是否还有可用 IPv4（按"排除热点网卡"的规则）。</summary>
        public static bool HasUsableLocalIp(GuardConfig config)
        {
            foreach (NetworkInterface nic in Usable())
            {
                try
                {
                    if (IsExcluded(nic.Name, nic.Description, config?.Network?.ExcludedInterfaces)) continue;
                    string ip, gateway;
                    if (IsOnline(nic, out ip, out gateway)) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
