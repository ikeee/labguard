using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 网络与防火墙守护（核心模块）：
    /// · 断网：网卡变 Down / IP 清空 / 0.0.0.0 / APIPA(169.254.*)，持续 N 秒确认 →
    ///   提示 + 可选全屏遮罩（插回网线自动消失）+ 可选限次响鸣；
    /// · 改 IP：与启动基线比对后强制还原（DHCP 的机器改回 source=dhcp）；
    /// · 防火墙：发现"阻止所有传入连接"被打开 → netsh 强制关闭。
    ///
    /// 关键：只把"真正代表本机联网"的网卡当作判据（见 <see cref="NicRoles"/>）。
    /// 一体机机房"有线为主 + 无线常开做热点"时，热点网卡（192.168.137.1 / Wi-Fi Direct）
    /// 既不该让断网判定失效，也不该被误判成断网。
    /// </summary>
    public sealed class NetworkGuard : GuardBase
    {
        public override string Name => "网络/防火墙";
        public override bool Enabled => Context?.Config.Network.Enabled ?? false;
        protected override int IntervalMs => 10000;

        /// <summary>断网持续这么多秒才判定（避免交换机抖动/无线漫游误报）。</summary>
        private const int DisconnectConfirmSeconds = 20;

        private class Baseline
        {
            public string Name;
            public string Ip;
            public string Mask;
            public string Gateway;
            public bool Dhcp;
            public bool WasUp;
            public bool Watched;
        }

        private readonly List<Baseline> _baseline = new List<Baseline>();
        private readonly HashSet<string> _watchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _startedAt;
        private DateTime _unpluggedSince = DateTime.MinValue;
        private bool _maskShown;
        private bool _soundDone;
        private bool _pausedByTeacher;
        public bool PausedByTeacher => _pausedByTeacher;

        protected override void OnStart()
        {
            _startedAt = DateTime.Now;
            string reason;
            List<NetworkInterface> watched = NicRoles.SelectWatched(Context.Config, out reason);
            foreach (NetworkInterface nic in watched) _watchedNames.Add(nic.Name);

            foreach (NetworkInterface nic in NicRoles.Usable())
            {
                Baseline b = Snapshot(nic);
                if (b == null) continue;
                b.Watched = _watchedNames.Contains(b.Name);
                _baseline.Add(b);
            }
            Log.Info("网络基线：" + string.Join("；", _baseline.Where(b => b.Ip != null)
                .Select(b => b.Name + "=" + b.Ip + (b.Dhcp ? "(DHCP)" : "") + (b.Watched ? "[判据]" : ""))));
            Log.Info("断网判据网卡：" + (_watchedNames.Count == 0 ? "（无）" : string.Join(", ", _watchedNames)) + " —— " + reason);
            SetStatus("监控 " + _watchedNames.Count + " 个网卡");
        }

        protected override void OnTick()
        {
            // 经验值：开机后要给电子教室/网络足够时间，默认 120 秒内不判违规
            if ((DateTime.Now - _startedAt).TotalSeconds < Context.Config.StartDelaySeconds)
            {
                SetStatus("预热中（" + Context.Config.StartDelaySeconds + "秒后开始检测）");
                return;
            }

            var current = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface nic in NicRoles.Usable())
            {
                Baseline b = Snapshot(nic);
                if (b != null) current[b.Name] = b;
            }

            int watchedTotal = 0, watchedDown = 0;
            foreach (Baseline baseLine in _baseline)
            {
                if (!baseLine.Watched) continue;
                watchedTotal++;
                Baseline now;
                bool down = !current.TryGetValue(baseLine.Name, out now) || IsDisconnected(now.Ip, now.WasUp);
                if (down) watchedDown++;
            }

            bool allDown = watchedTotal > 0 && watchedDown == watchedTotal;
            bool anyDown = watchedDown > 0;
            bool violationNow = Context.Config.Network.DisconnectRequireAllDown ? allDown : anyDown;

            if (violationNow && Context.Config.Network.DetectDisconnected)
            {
                if (_unpluggedSince == DateTime.MinValue) _unpluggedSince = DateTime.Now;
                double elapsed = (DateTime.Now - _unpluggedSince).TotalSeconds;

                if (elapsed >= DisconnectConfirmSeconds)
                {
                    Context.Report(Name, "检测到本机网络已断开：请插回网线或启用网络连接。",
                        HostAction(), string.Join("/", _watchedNames) + " 已断开 " + (int)elapsed + " 秒");
                    ShowMaskIfNeeded(_unpluggedSince);
                    MaybeSound(_unpluggedSince);
                }
                SetStatus("断网 " + (int)elapsed + " 秒（" + watchedDown + "/" + watchedTotal + " 个判据网卡断开）");
                return;   // 断网期间不做 IP 比对（读到的是无效值）
            }

            if (_unpluggedSince != DateTime.MinValue)
            {
                if (anyDown)
                {
                    SetStatus("部分判据网卡断开（仍有链路，不算断网）");
                }
                else
                {
                    Log.Info("网络已恢复，遮罩与响鸣标记复位");
                    _unpluggedSince = DateTime.MinValue;
                    _maskShown = false;
                    _soundDone = false;
                    SetStatus("正常");
                }
            }

            // 改 IP 检测（只对被判据网卡）
            bool violation = false;
            foreach (Baseline baseLine in _baseline)
            {
                if (!baseLine.Watched) continue;
                Baseline now;
                if (!current.TryGetValue(baseLine.Name, out now)) continue;
                if (now.Ip != null && baseLine.Ip != null && now.Ip != baseLine.Ip)
                {
                    violation = true;
                    Context.Report(Name, "本机 IP 被修改，原地址为：" + baseLine.Ip + "，现已强制恢复。",
                        HostAction(), baseLine.Name);
                    if (Context.Config.Network.RestoreOriginalIp) Restore(baseLine);
                }
            }

            // 防火墙
            if (Context.Config.Network.ForceFirewallOff && FirewallEnabled())
            {
                violation = true;
                Context.Report(Name, "检测到防火墙被开启，可能影响课堂广播；已自动关闭。",
                    ViolationAction.Notify);
                SystemActions.Run("netsh.exe", "advfirewall set allprofiles state off");
            }

            if (!violation) SetStatus("正常（判据 " + _watchedNames.Count + " 个网卡）");
        }

        // ------------------------------------------------------------------ 断网遮罩 / 响鸣
        private void ShowMaskIfNeeded(DateTime since)
        {
            if (!Context.Config.Network.DisconnectMask || _maskShown) return;
            _maskShown = true;
            if (Context.Alert == null)
            {
                Log.Warn("断网遮罩需要界面进程（服务侧无法显示），已跳过");
                return;
            }
            int chars = Math.Max(1, Context.Config.Wallpaper.MachineNumberChars);
            string name = Environment.MachineName;
            string number = name.Length <= chars ? name : name.Substring(name.Length - chars);
            bool randomWallpaper = !string.Equals(Context.Config.Network.DisconnectMaskBackground,
                "Plain", StringComparison.OrdinalIgnoreCase);

            Context.Alert.ShowDisconnectMask(
                "机位 " + number,
                "网络已断开",
                "请插回网线或启用网络连接",
                Context.Config.PasswordHash,
                randomWallpaper,
                Context.Config.Network.DisconnectMaskShowElapsed,
                () =>
                {
                    TimeSpan span = DateTime.Now - since;
                    return "已断开 " + (int)span.TotalMinutes + " 分 " + span.Seconds + " 秒 · 插回网线后 10 秒内自动恢复";
                },
                IsWatchedRestored,
                () =>
                {
                    Context.Report(Name, "老师用密码解除了断网遮罩，监控暂停。", ViolationAction.Notify, "教师解锁");
                    _pausedByTeacher = true;
                });
            Log.Warn("已弹出断网遮罩（机位 " + number + "）");
        }

        private void MaybeSound(DateTime since)
        {
            var n = Context.Config.Network;
            if (n.DisconnectSoundAfterSeconds <= 0 || _soundDone) return;
            if ((DateTime.Now - since).TotalSeconds < n.DisconnectSoundAfterSeconds) return;
            _soundDone = true;
            int times = Math.Max(1, n.DisconnectSoundTimes);
            int interval = Math.Max(200, n.DisconnectSoundIntervalMs);
            Context.Alert?.Beep(times, interval);
            Log.Warn("断网持续 " + n.DisconnectSoundAfterSeconds + " 秒，已响鸣 " + times + " 次");
        }

        /// <summary>
        /// 遮罩用它判断"网线插回来了没有"：必须**判据网卡**恢复可用 IP 才算。
        /// 不能只看"本机有没有 IP"——否则热点网卡的 192.168.137.1 会让遮罩误以为网络已恢复。
        /// </summary>
        public bool IsWatchedRestored()
        {
            if (_watchedNames.Count == 0) return NicRoles.HasUsableLocalIp(Context.Config);
            foreach (NetworkInterface nic in NicRoles.Usable())
            {
                if (!_watchedNames.Contains(nic.Name)) continue;
                string ip;
                if (NicRoles.HasUsableIpv4(nic, out ip) && ip != NicRoles.IcsGatewayIp) return true;
            }
            return false;
        }

        /// <summary>断网判定（纯函数，便于自检）：网卡 Down / IP 空 / 0.0.0.0 / APIPA。</summary>
        public static bool IsDisconnected(string ip, bool nicUp)
        {
            if (!nicUp) return true;
            if (string.IsNullOrEmpty(ip)) return true;
            if (ip == "0.0.0.0") return true;
            return ip.StartsWith("169.254.");
        }

        private ViolationAction HostAction()
        {
            if (Context.Config.Network.ShutdownOnViolation) return ViolationAction.Shutdown;
            if (Context.Config.Network.LockOnViolation) return ViolationAction.Lock;
            return ViolationAction.Notify;
        }

        private static Baseline Snapshot(NetworkInterface nic)
        {
            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                UnicastIPAddressInformation unicast = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                GatewayIPAddressInformation gw = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return new Baseline
                {
                    Name = nic.Name,
                    Ip = unicast?.Address.ToString(),
                    Mask = unicast?.IPv4Mask?.ToString(),
                    Gateway = gw?.Address.ToString(),
                    Dhcp = props.DhcpServerAddresses.Count > 0,
                    WasUp = nic.OperationalStatus == OperationalStatus.Up
                };
            }
            catch { return null; }
        }

        private void Restore(Baseline b)
        {
            string arg;
            if (b.Dhcp)
            {
                arg = "interface ip set address name=\"" + b.Name + "\" source=dhcp";
            }
            else
            {
                if (string.IsNullOrEmpty(b.Mask)) return;
                arg = "interface ip set address name=\"" + b.Name + "\" static " + b.Ip + " " + b.Mask +
                      (string.IsNullOrEmpty(b.Gateway) ? "" : " " + b.Gateway);
            }
            SystemActions.Run("netsh.exe", arg);
            SystemActions.Run("ipconfig.exe", "/flushdns");
        }

        private static bool FirewallEnabled()
        {
            string[] paths =
            {
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile",
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile",
                @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\DomainProfile"
            };
            foreach (string p in paths)
            {
                object v = SystemActions.GetRegistryValue(RegistryHive.LocalMachine, p, "EnableFirewall");
                if (v != null && Convert.ToInt32(v) == 1) return true;
            }
            return false;
        }

        protected override void OnStop() { }
    }
}
