using System;
using System.Collections.Generic;

namespace LabGuard.Core.Config
{
    /// <summary>总体配置。字段命名与"功能对照表"一一对应，便于和原版逐项对照。</summary>
    public class GuardConfig
    {
        public int Version { get; set; } = 1;
        public string PasswordHash { get; set; } = "";
        /// <summary>已安装/已启用管控的总开关；关闭后各 Guard 不再纠正系统状态。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>退出/暂停后自动恢复管控的时间（分钟），0 = 不自动恢复。</summary>
        public int ResumeAfterMinutes { get; set; } = 0;
        /// <summary>程序启动后多少秒才开始检测（避免开机误判，原版为 120 秒）。</summary>
        public int StartDelaySeconds { get; set; } = 120;

        public ClassroomSettings Classroom { get; set; } = new ClassroomSettings();
        public NetworkSettings Network { get; set; } = new NetworkSettings();
        public ProcessBlockSettings ProcessBlock { get; set; } = new ProcessBlockSettings();
        public FileCreationSettings FileCreation { get; set; } = new FileCreationSettings();
        public UsbSettings Usb { get; set; } = new UsbSettings();
        public HostsSettings Hosts { get; set; } = new HostsSettings();
        public BrowserSettings Browser { get; set; } = new BrowserSettings();
        public ShellSettings Shell { get; set; } = new ShellSettings();
        public SafeModeSettings SafeMode { get; set; } = new SafeModeSettings();
        public WallpaperSettings Wallpaper { get; set; } = new WallpaperSettings();
        public RegistryAclSettings RegistryAcl { get; set; } = new RegistryAclSettings();
        public WatchdogSettings Watchdog { get; set; } = new WatchdogSettings();

        public IEnumerable<KeyValuePair<string, bool>> Snapshot()
        {
            yield return new KeyValuePair<string, bool>("总开关", Enabled);
            yield return new KeyValuePair<string, bool>("电子教室保护", Classroom.Enabled);
            yield return new KeyValuePair<string, bool>("网络/防火墙", Network.Enabled);
            yield return new KeyValuePair<string, bool>("违规软件拦截", ProcessBlock.Enabled);
            yield return new KeyValuePair<string, bool>("新文件/下载监控", FileCreation.Enabled);
            yield return new KeyValuePair<string, bool>("USB存储设备", Usb.Enabled);
            yield return new KeyValuePair<string, bool>("hosts黑名单", Hosts.Enabled);
            yield return new KeyValuePair<string, bool>("浏览器策略", Browser.Enabled);
            yield return new KeyValuePair<string, bool>("任务栏/系统工具", Shell.Enabled);
            yield return new KeyValuePair<string, bool>("安全模式/启动菜单", SafeMode.Enabled);
            yield return new KeyValuePair<string, bool>("桌面壁纸与编号", Wallpaper.Enabled);
            yield return new KeyValuePair<string, bool>("注册表权限加固", RegistryAcl.Enabled);
            yield return new KeyValuePair<string, bool>("互相守护", Watchdog.Enabled);
        }
    }

    public class ClassroomSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>电子教室客户端进程名（原版：极域 StudentMain.exe / 红蜘蛛 REDAgent.exe / 锐捷 ClassMangerApp.exe）。</summary>
        public List<string> ProcessNames { get; set; } = new List<string> { "StudentMain.exe", "REDAgent.exe", "ClassMangerApp.exe" };
        /// <summary>电子教室客户端完整路径（设置程序里可手动选择；留空则自动探测）。</summary>
        public string MainExecutable { get; set; } = "";
        /// <summary>极域的两个关键服务名。</summary>
        public List<string> RequiredServices { get; set; } = new List<string> { "TopDomainClient", "TopDomainClientHelper" };
        /// <summary>检测到被挂起后是否强制恢复。</summary>
        public bool ResumeWhenSuspended { get; set; } = true;
        /// <summary>检测到被终止后是否重新拉起。</summary>
        public bool RelaunchWhenKilled { get; set; } = true;
        /// <summary>是否监控极域的频道/自动登录参数。</summary>
        public bool GuardELearningParameters { get; set; } = true;
    }

    public class NetworkSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 强制学生机 DNS 指向集中过滤服务器（例如教师机的 AdGuard）。
        /// 留空 = 不锁定 DNS（例如校园网用 DHCP 下发 DNS 的场景）。
        /// </summary>
        public List<string> DnsServers { get; set; } = new List<string>();
        public bool EnforceDns { get; set; } = true;
        /// <summary>关闭浏览器自带的加密 DNS（DoH）——这是绕过集中 DNS 过滤的最主要路径。</summary>
        public bool BlockDoh { get; set; } = true;

        /// <summary>IP/网卡异常时尝试恢复原 IP。</summary>
        public bool RestoreOriginalIp { get; set; } = true;
        /// <summary>出现 169.254.* （拔网线/禁用网卡）时告警。</summary>
        public bool DetectDisconnected { get; set; } = true;
        /// <summary>发现防火墙开启（阻止所有传入连接）时强制关闭。</summary>
        public bool ForceFirewallOff { get; set; } = true;
        /// <summary>违规时是否全屏锁定（原版为提示 + 可选锁屏）。</summary>
        public bool LockOnViolation { get; set; } = false;
        /// <summary>违规时是否强制重启（原版 shutdown /s /f /t 0）。</summary>
        public bool ShutdownOnViolation { get; set; } = false;

        // ---------------- 断网遮罩（"屏保式"提示，插回网线自动消失） ----------------
        /// <summary>断网确认后弹出全屏遮罩。</summary>
        public bool DisconnectMask { get; set; } = true;
        /// <summary>遮罩背景：Wallpaper = 从安装目录 wallpaper 里随机选一张；Plain = 纯色提示页。</summary>
        public string DisconnectMaskBackground { get; set; } = "Wallpaper";
        /// <summary>只监控这些网卡（填网卡名，一行一个；留空 = 自动选择联网网卡）。</summary>
        public List<string> WatchedInterfaces { get; set; } = new List<string>();
        /// <summary>
        /// **只把有线网卡当作判据**（默认开）：无线/热点不参与"断网"判定。
        /// 一体机机房（有线为主 + 无线常开做热点）用这一项即可，不必手填网卡名；
        /// 若本机找不到有线网卡，会自动退回"自动选择"并写日志，不会静默失去监控。
        /// </summary>
        public bool WatchWiredOnly { get; set; } = true;
        /// <summary>
        /// 忽略的网卡（关键字匹配，一行一个）。默认忽略热点/虚拟网卡——
        /// 「一体机做热点」的机房必须忽略 Wi-Fi Direct / 移动热点，否则热点一关就被误判成"拔网线"。
        /// </summary>
        public List<string> ExcludedInterfaces { get; set; } = new List<string>
        {
            "Wi-Fi Direct", "WiFi Direct", "本地连接* 1", "本地连接* 2", "移动热点", "Mobile Hotspot",
            "Hyper-V", "VMware", "VirtualBox", "TAP-", "Tailscale", "ZeroTier", "Bluetooth", "Loopback",
            "Virtual Adapter"
        };
        /// <summary>
        /// 判定"断网"的条件：true = 被监控网卡**全部**断开才算（推荐，适合无线也可能联网的机器）；
        /// false = 任意一个断开就算（适合"有线是唯一链路"的机房）。
        /// </summary>
        public bool DisconnectRequireAllDown { get; set; } = true;
        /// <summary>DNS 锁定只作用在被监控网卡上（不动热点 / 无线的 DNS）。</summary>
        public bool DnsLockOnlyWatchedInterfaces { get; set; } = true;
        /// <summary>遮罩上显示倒计时（秒），仅表示"已断开多久"。</summary>
        public bool DisconnectMaskShowElapsed { get; set; } = true;
        /// <summary>断网持续这么多秒后开始响鸣；0 = 不响。</summary>
        public int DisconnectSoundAfterSeconds { get; set; } = 60;
        /// <summary>响鸣次数上限（防止变成"学生可控的噪音武器"）。</summary>
        public int DisconnectSoundTimes { get; set; } = 3;
        /// <summary>响鸣间隔（毫秒）。</summary>
        public int DisconnectSoundIntervalMs { get; set; } = 700;
    }

    public class ProcessBlockSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>破解/控制类工具。</summary>
        public bool BlockClassroomCrack { get; set; } = true;
        /// <summary>进程/内核/注册表工具。</summary>
        public bool BlockProcessTools { get; set; } = true;
        /// <summary>虚拟桌面类。</summary>
        public bool BlockVirtualDesktop { get; set; } = true;
        /// <summary>杀毒/管家软件（对应原版 shadu_jianche）。</summary>
        public bool BlockAntiVirus { get; set; } = true;
        /// <summary>解压软件（对应原版禁止解压工具）。</summary>
        public bool BlockArchivers { get; set; } = true;
        /// <summary>任务管理器（对应原版 gaowei / 窗口标题）。</summary>
        public bool BlockTaskManager { get; set; } = true;
        /// <summary>注册表编辑器（对应原版 zcb）。</summary>
        public bool BlockRegedit { get; set; } = true;
        /// <summary>命令提示符 / PowerShell（对应原版 cmd_jianche）。</summary>
        public bool BlockCommandPrompt { get; set; } = false;
        /// <summary>小游戏（扫雷/纸牌等 + Image File Execution Options 禁用）。</summary>
        public bool BlockGames { get; set; } = true;
        /// <summary>发现违规时的动作：Notify / Kill / Lock / Shutdown / Reboot。</summary>
        public string Action { get; set; } = "Kill";
        public List<string> ExtraKeywords { get; set; } = new List<string>();
    }

    public class FileCreationSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>AllowAll / CppOnly / DenyAll（对应原版 12 组开关里的三档）。</summary>
        public string Mode { get; set; } = "CppOnly";
        public List<string> WatchPaths { get; set; } = new List<string>
        {
            @"%USERPROFILE%\Downloads", @"D:\", @"E:\", @"F:\", @"C:\"
        };
        public List<string> HighRiskExtensions { get; set; } = new List<string>
        {
            ".zip", ".rar", ".7z", ".arj", ".exe", ".com", ".msi", ".reg", ".bat", ".cmd",
            ".vbs", ".vbe", ".scr", ".dll", ".ps1", ".pif"
        };
        public List<string> AllowedExtensions { get; set; } = new List<string> { ".cpp", ".cp", ".h", ".txt" };
        /// <summary>动作：Delete / Lock / Notify。</summary>
        public string Action { get; set; } = "Delete";
    }

    public class UsbSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>允许 USB 存储设备（false = 禁用 usbstor 驱动）。</summary>
        public bool AllowStorage { get; set; } = false;
        /// <summary>保留 USB 键鼠（只禁存储类，不禁用整个 USB）。</summary>
        public bool KeepHidDevices { get; set; } = true;
    }

    public class HostsSettings
    {
        /// <summary>
        /// 本地 hosts 黑名单。**默认关闭**——有集中 DNS（教师机 AdGuard）时不需要它，
        /// 集中 DNS 对所有浏览器/程序统一生效，且没有本地文件可篡改。
        /// </summary>
        public bool Enabled { get; set; } = false;
        /// <summary>即使不用黑名单，也给 hosts 文件加 ACL（只读），防止学生自己往 hosts 写映射绕过集中 DNS。</summary>
        public bool ProtectFileAcl { get; set; } = true;
        /// <summary>额外追加的域名（除内置清单外）。</summary>
        public List<string> ExtraDomains { get; set; } = new List<string>();
        public bool UseBuiltInDomains { get; set; } = true;
        /// <summary>把域名解析到 0.0.0.0（比 127.0.0.1 更彻底，本地无服务时直接失败）。</summary>
        public string SinkAddress { get; set; } = "127.0.0.1";
        /// <summary>隐藏 hosts 文件并关闭"显示隐藏文件/显示扩展名"。</summary>
        public bool HideHostsFile { get; set; } = true;
        /// <summary>轮询守护（被删/被改就恢复）。</summary>
        public bool Guard { get; set; } = true;
    }

    public class BrowserSettings
    {
        public bool Enabled { get; set; } = true;
        public bool BlockDownloads { get; set; } = true;
        public bool BlockSaveAs { get; set; } = true;
        public bool BlockDevTools { get; set; } = true;
        public bool BlockChromeDino { get; set; } = true;
        public bool BlockEdgeSurf { get; set; } = true;
        public bool BlockIeDownload { get; set; } = true;
        /// <summary>首页/导航站（留空 = 不改学生浏览器主页）。</summary>
        public string Homepage { get; set; } = "";
        /// <summary>用 LabGuard.Launcher 替换浏览器快捷方式（默认关；开启后点浏览器会先经过本程序）。</summary>
        public bool HijackShortcuts { get; set; } = false;
    }

    public class ShellSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>隐藏任务视图（虚拟桌面）按钮。</summary>
        public bool HideTaskView { get; set; } = true;
        /// <summary>屏蔽 Win 键（防虚拟桌面脱离控制）。</summary>
        public bool BlockWindowsKey { get; set; } = true;
        /// <summary>禁用任务栏右键菜单。</summary>
        public bool NoTrayContextMenu { get; set; } = true;
        public bool DisableTaskManager { get; set; } = true;
        public bool DisableRegistryTools { get; set; } = true;
        public bool DisableCmd { get; set; } = false;
        public bool DisableLockWorkstation { get; set; } = false;
        public bool HideFileExtensions { get; set; } = true;
        public bool NoFolderOptions { get; set; } = true;
    }

    public class SafeModeSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// **在"安全模式"（含无网络的安全模式）里是否继续管控**。默认 false = 不加载、不干预，
        /// 给老师留一条后路：学生机出问题/不再使用/要彻底卸载时，进安全模式就能下手。
        /// </summary>
        public bool RunInSafeMode { get; set; } = false;
        /// <summary>删除 SafeBoot\Network（禁止"带网络连接的安全模式"）。</summary>
        public bool BlockNetworkSafeMode { get; set; } = true;
        /// <summary>隐藏开机启动菜单。</summary>
        public bool HideBootMenu { get; set; } = true;
    }

    public class WallpaperSettings
    {
        public bool Enabled { get; set; } = false;
        /// <summary>壁纸图片路径（留空 = 使用安装目录 wallpaper\ 下的图片）。</summary>
        public string Image { get; set; } = "";
        /// <summary>右上角显示机器编号（计算机名后 N 位）。</summary>
        public bool ShowMachineNumber { get; set; } = true;
        public int MachineNumberChars { get; set; } = 6;
        public bool GuardWallpaper { get; set; } = true;
    }

    public class RegistryAclSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>被加固的注册表键（拒绝普通用户写入）。</summary>
        public List<string> Keys { get; set; } = new List<string>
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\LabGuard"
        };
    }

    public class WatchdogSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>程序文件被删除/被改时，从"系统级备份副本"自动恢复（默认开）。</summary>
        public bool RestoreMissingFiles { get; set; } = true;
        /// <summary>心跳上报地址（可选）：填了之后每 60 秒把本机状态 POST 给教师机上的监视器。</summary>
        public string ReportUrl { get; set; } = "";
        /// <summary>服务异常退出后自动重启电脑（对应原版 sc failure … actions=reboot）。</summary>
        public bool RebootOnServiceFailure { get; set; } = false;
        /// <summary>代理进程被结束后自动重新拉起的间隔（秒）。</summary>
        public int AgentRestartSeconds { get; set; } = 10;
        /// <summary>关键文件校验失败时是否进入锁定。</summary>
        public bool LockOnIntegrityFailure { get; set; } = true;
    }
}
