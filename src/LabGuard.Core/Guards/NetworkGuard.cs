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
    /// 网络与防火墙守护。
    ///
    /// 断网判定以“物理网卡链路是否完整”为准：网卡必须 Up、拿到 IPv4、
    /// 有默认网关，才算在线。网卡枚举为空也视为离线，避免开机前拔线时
    /// 因为 Windows 还没枚举出网卡而漏判。
    /// </summary>
    public sealed class NetworkGuard : GuardBase
    {
        public override string Name => "网络/防火墙";
        public override bool Enabled => Context?.Config.Network.Enabled ?? false;
        protected override int IntervalMs => 10000;

        /// <summary>第一次、第二次断网各确认 60 秒；第三次起缩短为 5 秒。</summary>
        private const int DisconnectConfirmSeconds = 60;
        private const int RepeatDisconnectConfirmSeconds = 5;
        private const int RepeatDisconnectAfterCount = 2;
        /// <summary>连续稳定在线 10 分钟后，重复断网计数复位。</summary>
        private const int RepeatResetMinutes = 10;

        private sealed class LinkSnapshot
        {
            public string Name;
            public string Ip;
            public string Mask;
            public string Gateway;
            public bool Dhcp;
            public bool WasUp;
            public bool Watched;

            public bool HasGateway
            {
                get { return !string.IsNullOrEmpty(Gateway) && Gateway != "0.0.0.0"; }
            }

            public bool IsOnline
            {
                get { return IsLinkOnline(WasUp, Ip, Gateway); }
            }
        }

        private readonly List<LinkSnapshot> _baseline = new List<LinkSnapshot>();
        private readonly HashSet<string> _watchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _startedAt;
        private DateTime _unpluggedSince = DateTime.MinValue;
        private long _unpluggedStamp;
        private DateTime _lastWall = DateTime.MinValue;
        private double _lastStamp;
        private bool _maskShown;
        private bool _soundDone;
        private int _confirmedDisconnects;
        private bool _episodeCounted;
        private DateTime _stableSince = DateTime.MinValue;
        private bool _pausedByTeacher;
        private string _downReason = "";

        public bool PausedByTeacher => _pausedByTeacher;

        private static double MonoSeconds()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp() /
                   (double)System.Diagnostics.Stopwatch.Frequency;
        }

        public static string DescribeDown(bool nicExists, bool wasUp, string ip, string gateway = null)
        {
            if (!nicExists) return "判据网卡已消失（网卡被禁用或已被识别不到）";
            if (!wasUp) return "网卡已断开（网线被拔掉，或网卡被禁用）";
            if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0") return "IP 已被清空（没拿到地址）";
            if (ip.StartsWith("169.254.", StringComparison.Ordinal))
                return "只拿到 169.254.* 自动地址（网线未接好，或 DHCP 不可达）";
            if (string.IsNullOrEmpty(gateway) || gateway == "0.0.0.0") return "没有默认网关（网络不完整）";
            return "网络不可用";
        }

        protected override void OnStart()
        {
            _startedAt = DateTime.Now;
            _stableSince = _startedAt;
            RefreshBaseline();

            Log.Info("网络基线：" + string.Join("；", _baseline.Where(b => b.Ip != null)
                .Select(b => b.Name + "=" + b.Ip + (b.Dhcp ? "(DHCP)" : "") + (b.Watched ? "[判据]" : ""))));
            Log.Info("断网判据网卡：" + (_watchedNames.Count == 0 ? "（无）" : string.Join(", ", _watchedNames)));

            // 先做一轮探测：如果开机前就拔了网线，计时从服务启动即开始，
            // 不再等 Windows 慢慢初始化网卡或等下一次 10 秒轮询。
            try { OnTick(); }
            catch (Exception ex) { Log.Warn("网络首次探测失败：" + ex.Message); }
        }

        private void RefreshBaseline()
        {
            _baseline.Clear();
            _watchedNames.Clear();

            string reason;
            foreach (NetworkInterface nic in NicRoles.SelectWatched(Context.Config, out reason))
                _watchedNames.Add(nic.Name);

            foreach (NetworkInterface nic in NicRoles.Usable())
            {
                LinkSnapshot snapshot = Snapshot(nic);
                if (snapshot == null) continue;
                snapshot.Watched = _watchedNames.Contains(snapshot.Name);
                _baseline.Add(snapshot);
            }

            if (_watchedNames.Count == 0)
                Log.Warn("没有找到物理网卡判据；按离线处理，等待网卡枚举恢复");
            else
                Log.Info("网络判据：" + string.Join(", ", _watchedNames) + " —— " + reason);
        }

        protected override void OnTick()
        {
            DateTime wall = DateTime.Now;
            double mono = MonoSeconds();
            if (_lastWall != DateTime.MinValue)
            {
                double wallStep = (wall - _lastWall).TotalSeconds;
                double monoStep = mono - _lastStamp;
                if (Math.Abs(wallStep - monoStep) > 60)
                    Context.Report(Name, "检测到系统时间被改动（墙钟与运行时钟偏差 "
                        + (int)(wallStep - monoStep) + " 秒）——可能是在对抗断网确认计时。",
                        ViolationAction.Notify, "时间跳变");
            }
            _lastWall = wall;
            _lastStamp = mono;

            Dictionary<string, LinkSnapshot> current = SnapshotAll();

            // 冷启动时 NicRoles 可能第一轮返回空；后续一旦 Windows 枚举出网卡，
            // 必须自动补上判据，而不是把 watchedTotal=0 当成“没有断网”。
            if (_watchedNames.Count == 0 || !_baseline.Any(b => b.Watched))
            {
                RefreshBaseline();
                current = SnapshotAll();
            }

            int watchedTotal = 0;
            int watchedDown = 0;
            _downReason = "";

            foreach (LinkSnapshot baseLine in _baseline)
            {
                if (!baseLine.Watched) continue;
                watchedTotal++;
                LinkSnapshot now;
                bool exists = current.TryGetValue(baseLine.Name, out now);
                bool online = exists && now != null && now.IsOnline;
                if (!online)
                {
                    watchedDown++;
                    if (_downReason.Length == 0)
                    {
                        _downReason = baseLine.Name + " " +
                            DescribeDown(exists, exists && now != null && now.WasUp,
                                exists && now != null ? now.Ip : null,
                                exists && now != null ? now.Gateway : null);
                    }
                }
            }

            bool noPhysical = current.Count == 0 || watchedTotal == 0;
            bool allDown = watchedTotal > 0 && watchedDown == watchedTotal;
            bool anyDown = watchedDown > 0;
            bool violationNow;
            if (noPhysical)
            {
                violationNow = Context.Config.Network.DetectDisconnected;
                if (_downReason.Length == 0) _downReason = "没有找到可判定的物理网卡（枚举为空）";
            }
            else
            {
                violationNow = Context.Config.Network.DisconnectRequireAllDown ? allDown : anyDown;
            }

            bool inWarmup = (DateTime.Now - _startedAt).TotalSeconds <
                            Math.Max(0, Context.Config.StartDelaySeconds);
            // 预热只推迟“正常联网时的 IP/网关基线校正”；已经断网时不能
            // 被预热挡住，否则开机前拔线仍会等 60 秒后再开始计时。
            if (inWarmup && !violationNow)
            {
                SetStatus("预热中（" + Context.Config.StartDelaySeconds + "秒后做 IP/网关校正）");
                return;
            }

            if (violationNow && Context.Config.Network.DetectDisconnected)
            {
                if (_unpluggedStamp == 0)
                {
                    _unpluggedStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    _unpluggedSince = DateTime.Now;
                    _stableSince = DateTime.MinValue;
                    _episodeCounted = false;
                }

                double elapsed = MonoSeconds() - (_unpluggedStamp /
                                                   (double)System.Diagnostics.Stopwatch.Frequency);
                int confirmSeconds = ConfirmSecondsFor(_confirmedDisconnects);
                if (elapsed >= confirmSeconds)
                {
                    Context.Report(Name, "检测到本机网络已断开（" + _downReason +
                        "）：请插回网线或启用网络连接。", HostAction(),
                        string.Join("/", _watchedNames) + " 已断开 " + (int)elapsed + " 秒");
                    ShowMaskIfNeeded(_unpluggedSince);
                    MaybeSound(_unpluggedSince);
                    if (!_episodeCounted)
                    {
                        _episodeCounted = true;
                        _confirmedDisconnects++;
                        Log.Warn("已确认第 " + _confirmedDisconnects +
                                 " 次断网（本次确认阈值 " + confirmSeconds + " 秒）");
                    }
                }
                SetStatus("断网 " + (int)elapsed + " 秒（确认阈值 " + confirmSeconds +
                          " 秒，" + watchedDown + "/" + Math.Max(1, watchedTotal) + " 个判据网卡断开）");
                return;
            }

            if (_unpluggedStamp != 0)
            {
                if (anyDown && !Context.Config.Network.DisconnectRequireAllDown)
                {
                    SetStatus("部分判据网卡断开（仍有链路，不算断网）");
                }
                else
                {
                    Log.Info("网络已恢复，遮罩与响铃标记复位");
                    ResetAfterRestore();
                    SetStatus("正常");
                }
            }

            if (_confirmedDisconnects > 0 && _stableSince != DateTime.MinValue &&
                ShouldResetRepeat(_confirmedDisconnects, DateTime.Now - _stableSince))
            {
                Log.Info("网络已稳定 " + RepeatResetMinutes + " 分钟，重复断网计数复位（原 " +
                         _confirmedDisconnects + " 次）");
                _confirmedDisconnects = 0;
                _stableSince = DateTime.MinValue;
            }

            bool violation = false;
            if (!inWarmup)
            {
                foreach (LinkSnapshot baseLine in _baseline)
                {
                    if (!baseLine.Watched) continue;
                    LinkSnapshot now;
                    if (!current.TryGetValue(baseLine.Name, out now)) continue;
                    if (now.Ip != null && baseLine.Ip != null && now.Ip != baseLine.Ip)
                    {
                        violation = true;
                        Context.Report(Name, "本机 IP 被修改，原地址为：" + baseLine.Ip +
                            "，现已强制恢复。", HostAction(), baseLine.Name);
                        if (Context.Config.Network.RestoreOriginalIp) Restore(baseLine);
                    }
                    if (!string.IsNullOrEmpty(baseLine.Gateway) && now.Gateway != baseLine.Gateway)
                    {
                        violation = true;
                        Context.Report(Name, "默认网关被修改（" + (baseLine.Gateway ?? "（无）") +
                            " → " + (now.Gateway ?? "（已删除）") + "），网络实际已不通。",
                            HostAction(), baseLine.Name);
                        if (Context.Config.Network.RestoreOriginalIp) Restore(baseLine);
                    }
                }
            }

            if (Context.Config.Network.ForceFirewallOff && FirewallEnabled())
            {
                violation = true;
                Context.Report(Name, "检测到防火墙被打开，可能影响课堂广播；已自动关闭。",
                    ViolationAction.Notify);
                SystemActions.Run("netsh.exe", "advfirewall set allprofiles state off");
            }

            if (!violation) SetStatus("正常（判据 " + _watchedNames.Count + " 个网卡）");
        }

        private Dictionary<string, LinkSnapshot> SnapshotAll()
        {
            var current = new Dictionary<string, LinkSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface nic in NicRoles.Usable())
            {
                LinkSnapshot snapshot = Snapshot(nic);
                if (snapshot != null) current[snapshot.Name] = snapshot;
            }
            return current;
        }

        private void ShowMaskIfNeeded(DateTime since)
        {
            if (!Context.Config.Network.DisconnectMask || _maskShown) return;
            _maskShown = true;
            if (Context.Alert == null)
            {
                Log.Warn("断网遮罩需要界面进程（服务侧无法显示），已跳过");
                return;
            }
            int chars = Math.Max(1, Context.Config.Site.MachineNumberChars);
            string name = Environment.MachineName;
            string number = name.Length <= chars ? name : name.Substring(name.Length - chars);
            bool randomWallpaper = !string.Equals(Context.Config.Network.DisconnectMaskBackground,
                "Plain", StringComparison.OrdinalIgnoreCase);

            string machineNumber = Context.Config.Site.ShowMachineNumber ? ("机位 " + number) : "本机";
            Context.Alert.ShowDisconnectMask(
                machineNumber,
                "网络已断开",
                "检测到：" + _downReason + "\r\n请插回网线或启用网络连接",
                Context.Config.PasswordHash,
                randomWallpaper,
                Context.Config.Network.DisconnectMaskShowElapsed,
                () =>
                {
                    TimeSpan span = DateTime.Now - since;
                    return "已断开 " + (int)span.TotalMinutes + " 分 " + span.Seconds + " 秒 · 插回网线后自动恢复";
                },
                IsWatchedRestored,
                channel =>
                {
                    if (string.IsNullOrEmpty(channel)) channel = "管理员通道";
                    bool pause = Context.Config.Network.PauseMonitoringOnTeacherUnlock;
                    Context.Report(Name,
                        pause ? "老师解除了断网遮罩，监控暂停。"
                              : "老师解除了断网遮罩（按设置继续监控）。",
                        ViolationAction.Notify, "老师解锁");
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
            Log.Warn("断网持续 " + n.DisconnectSoundAfterSeconds + " 秒，已响铃 " + times + " 次");
        }

        /// <summary>
        /// 遮罩自动消失前必须重新枚举网卡，并和 NetworkGuard 使用同一口径：
        /// Up + IPv4 + 默认网关。
        /// </summary>
        public bool IsWatchedRestored()
        {
            string reason;
            List<NetworkInterface> watched = NicRoles.SelectWatched(Context.Config, out reason);
            if (watched.Count == 0) return false;

            bool anyOnline = false;
            bool allOnline = true;
            foreach (NetworkInterface nic in watched)
            {
                string ip, gateway;
                bool online = NicRoles.IsOnline(nic, out ip, out gateway);
                if (online) anyOnline = true;
                else allOnline = false;
            }

            bool restored = Context.Config.Network.DisconnectRequireAllDown ? allOnline : anyOnline;
            if (restored)
            {
                ResetAfterRestore();
                Log.Info("遮罩检测到网络已恢复，断网状态已复位（可再次触发遮罩）");
            }
            return restored;
        }

        private void ResetAfterRestore()
        {
            _unpluggedSince = DateTime.MinValue;
            _unpluggedStamp = 0;
            _maskShown = false;
            _soundDone = false;
            _episodeCounted = false;
            _stableSince = DateTime.Now;
            _pausedByTeacher = false;
        }

        /// <summary>前两次 60 秒；已确认两次后，从第三次起使用 5 秒。</summary>
        public static int ConfirmSecondsFor(int confirmedDisconnects)
        {
            return confirmedDisconnects >= RepeatDisconnectAfterCount
                ? RepeatDisconnectConfirmSeconds
                : DisconnectConfirmSeconds;
        }

        /// <summary>连续稳定在线满 10 分钟才复位重复断网计数。</summary>
        public static bool ShouldResetRepeat(int confirmedDisconnects, TimeSpan stableFor)
        {
            return confirmedDisconnects > 0 && stableFor.TotalMinutes >= RepeatResetMinutes;
        }

        /// <summary>网卡链路是否完整：Up + 可用 IPv4 + 默认网关。</summary>
        public static bool IsLinkOnline(bool nicUp, string ip, string gateway)
        {
            if (!nicUp || string.IsNullOrEmpty(ip)) return false;
            if (ip == "0.0.0.0" || ip.StartsWith("169.254.", StringComparison.Ordinal)) return false;
            if (ip == NicRoles.IcsGatewayIp) return false;
            return !string.IsNullOrEmpty(gateway) && gateway != "0.0.0.0";
        }

        /// <summary>纯函数：旧逻辑兼容用；实际断网还要求默认网关。</summary>
        public static bool IsDisconnected(string ip, bool nicUp)
        {
            if (!nicUp) return true;
            if (string.IsNullOrEmpty(ip)) return true;
            if (ip == "0.0.0.0") return true;
            return ip.StartsWith("169.254.", StringComparison.Ordinal);
        }

        private ViolationAction HostAction()
        {
            if (Context.Config.Network.ShutdownOnViolation) return ViolationAction.Shutdown;
            if (Context.Config.Network.LockOnViolation) return ViolationAction.Lock;
            return ViolationAction.Notify;
        }

        private static LinkSnapshot Snapshot(NetworkInterface nic)
        {
            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                UnicastIPAddressInformation unicast = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                GatewayIPAddressInformation gateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return new LinkSnapshot
                {
                    Name = nic.Name,
                    Ip = unicast?.Address.ToString(),
                    Mask = unicast?.IPv4Mask?.ToString(),
                    Gateway = gateway?.Address.ToString(),
                    Dhcp = props.DhcpServerAddresses.Count > 0,
                    WasUp = nic.OperationalStatus == OperationalStatus.Up
                };
            }
            catch { return null; }
        }

        private void Restore(LinkSnapshot b)
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
