using System;
using System.Collections.Generic;

namespace LabGuard.Core.Config
{
    /// <summary>总体配置。字段命名与"功能对照表"一一对应，便于和逐项对照。</summary>
    public class GuardConfig
    {
        public int Version { get; set; } = 1;
        public string PasswordHash { get; set; } = "";
        /// <summary>
        /// 全屏锁定 / 断网遮罩期间是否把鼠标键盘一起锁住（"Off" / "On"）。
        /// 默认 On：学生按什么都没反应；老师按 Ctrl+Alt+Del 可取回输入（Windows 保留通道）。
        /// </summary>
        public string InputHardLock { get; set; } = "On";
        /// <summary>
        /// 危险操作（卸载 / 退出监控）确认前的等待秒数：按钮显示倒计时，期间不可点。
        /// 0 = 不等待（不推荐）。默认 10 秒。
        /// </summary>
        public int ConfirmDelaySeconds { get; set; } = 10;
        /// <summary>已安装/已启用管控的总开关；关闭后各 Guard 不再纠正系统状态。</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// 退出/暂停后自动恢复管控的时间（分钟），0 = 不自动恢复（需老师手动）。
        /// 默认 120：真机教训是老师解锁遮罩后**忘了自己还处在暂停态**，机器一整天裸奔；
        /// 取 2 小时是折中——够长（不会在一节课中途自己恢复），又能在跨节课/午休后自愈。
        /// </summary>
        public int ResumeAfterMinutes { get; set; } = 120;
        /// <summary>程序启动后多少秒才开始检测（避免开机误判，为 120 秒）。</summary>
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
        public SiteSettings Site { get; set; } = new SiteSettings();
        public RegistryAclSettings RegistryAcl { get; set; } = new RegistryAclSettings();
        public WatchdogSettings Watchdog { get; set; } = new WatchdogSettings();
        public AntiTamperSettings AntiTamper { get; set; } = new AntiTamperSettings();

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
            yield return new KeyValuePair<string, bool>("机位编号", Site.ShowMachineNumber);
            yield return new KeyValuePair<string, bool>("注册表权限加固", RegistryAcl.Enabled);
            yield return new KeyValuePair<string, bool>("互相守护", Watchdog.Enabled);
            yield return new KeyValuePair<string, bool>("进程防拆加固", AntiTamper.Enabled);
        }
    }

    public class ClassroomSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// 需要盯住的课堂软件进程名（**存在就防挂起，不存在不报告**——避免"广播时才启动"的进程每轮误报）。
        /// 极域 StudentMain / 红蜘蛛 REDAgent / 锐捷 ClassMangerApp / 噢易 MultiClient、Ctsc_Multi、VoiClient、TrayClient。
        /// </summary>
        public List<string> ProcessNames { get; set; } = new List<string>
        {
            "StudentMain.exe", "REDAgent.exe", "ClassMangerApp.exe",
            "MultiClient.exe", "Ctsc_Multi.exe", "VoiClient.exe", "TrayClient.exe"
        };
        /// <summary>关键客户端完整路径（必须常驻：丢了就报告并重新拉起；留空则自动探测）。</summary>
        public string MainExecutable { get; set; } = "";
        /// <summary>必须运行的服务（被停就重启）。极域两个 + 噢易教学系统的 MMPC、云桌面的 VoiClient。</summary>
        public List<string> RequiredServices { get; set; } = new List<string>
        {
            "TopDomainClient", "TopDomainClientHelper", "MMPC", "VoiClient"
        };
        /// <summary>检测到被挂起后是否强制恢复。</summary>
        public bool ResumeWhenSuspended { get; set; } = true;
        /// <summary>检测到被终止后是否重新拉起。</summary>
        public bool RelaunchWhenKilled { get; set; } = true;
        /// <summary>是否监控极域的频道/自动登录参数。</summary>
        public bool GuardELearningParameters { get; set; } = true;
        /// <summary>是否监控噢易 VOI 云桌面的服务器地址（gtserver）。</summary>
        public bool GuardVoiParameters { get; set; } = true;
        /// <summary>
        /// 云桌面服务器地址的期望值（如 10.28.254.254）。**留空 = 只检查合法性、不猜着写**；
        /// 填了就严格还原（适合地址固定的机房）。
        /// </summary>
        public string VoiGtServer { get; set; } = "";
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
        /// <summary>违规时是否全屏锁定（）。</summary>
        public bool LockOnViolation { get; set; } = false;
        /// <summary>违规时是否强制重启（）。</summary>
        public bool ShutdownOnViolation { get; set; } = false;

        // ---------------- 断网遮罩（"屏保式"提示，插回网线自动消失） ----------------
        /// <summary>断网确认后弹出全屏遮罩。</summary>
        public bool DisconnectMask { get; set; } = true;
        /// <summary>
        /// 遮罩背景：Plain = 纯深色提示页（默认）；Wallpaper = 从安装目录 wallpaper 里随机选一张。
        /// 注意：两种都只是**遮罩这个窗口自己怎么画**，与系统壁纸无关 —— 本程序从不修改学生机壁纸。
        /// </summary>
        public string DisconnectMaskBackground { get; set; } = "Plain";
        /// <summary>
        /// 老师解除遮罩的方式：Gesture = 按键手势（Pause → ↑↑↓↓←→←→）；
        /// Password = 连按 5 次 Esc + 密码；Both = 两条通道都可用（默认）。
        /// 取舍见 <c>docs/06</c>：手势**更可靠**（全程不必解除硬锁、不弹输入框），但**保密性差于密码**
        /// （Konami Code 是公开梗，且泄露后不像密码那样能改）。故默认保留双通道。
        /// </summary>
        public string TeacherUnlockMode { get; set; } = "Both";
        /// <summary>手势的激活键：Pause（默认，即 Pause/Break）/ ScrollLock。**只能是单键**（Ctrl+Pause 区分不出来）。</summary>
        public string UnlockGestureArmKey { get; set; } = "Pause";
        /// <summary>解锁序列，逗号分隔，只接受 U/D/L/R（上/下/左/右）。默认 ↑↑↓↓←→←→。</summary>
        public string UnlockGestureSequence { get; set; } = "U,U,D,D,L,R,L,R";
        /// <summary>激活后必须在此秒数内输完，超时自动重置（1~60 秒）。</summary>
        public int UnlockGestureWindowSeconds { get; set; } = 5;
        /// <summary>是否在遮罩上显示手势进度点。**学生也看得见**，默认关。</summary>
        public bool UnlockGestureFeedback { get; set; } = false;
        /// <summary>
        /// 遮罩上是否**提示老师怎么解除**（手势序列 / 连按 Esc 输密码）。默认**关**。
        /// 关键：遮罩是给学生看的，把解锁方式印上去等于把钥匙挂在锁上 ——
        /// 学生照着屏幕就能解。关掉后遮罩只说"恢复网络后自动消失"，老师按自己的方式解除即可。
        /// 只有新手老师培训期才值得临时打开（方法另见 docs/06 与设置程序里的说明）。
        /// </summary>
        public bool MaskShowTeacherHint { get; set; } = false;
        /// <summary>
        /// 老师解除遮罩后**是否顺带暂停全部监控**（默认开：否则 10 秒后遮罩又弹出来）。
        /// 关掉则只解除本次遮罩、USB/软件拦截等继续生效（网络 Guard 自己还会再弹遮罩，
        /// 适合"网线修好后就自动恢复"的场景）。注意：无论本项是开是关，
        /// **进程守护都不受影响**（见 <c>AntiTamper.KeepAliveWhenPaused</c>）。
        /// </summary>
        public bool PauseMonitoringOnTeacherUnlock { get; set; } = true;
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
        /// <summary>杀毒/管家软件（shadu_jianche）。</summary>
        public bool BlockAntiVirus { get; set; } = true;
        /// <summary>解压软件（禁止解压工具）。</summary>
        public bool BlockArchivers { get; set; } = true;
        /// <summary>任务管理器（gaowei / 窗口标题）。</summary>
        public bool BlockTaskManager { get; set; } = true;
        /// <summary>注册表编辑器（zcb）。</summary>
        public bool BlockRegedit { get; set; } = true;
        /// <summary>命令提示符 / PowerShell（cmd_jianche）。</summary>
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
        /// <summary>AllowAll / CppOnly / DenyAll（12 组开关里的三档）。</summary>
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
        /// <summary>
        /// 上网入口用哪个浏览器（完整 exe 路径）；留空 = 自动探测
        /// （注册表里的默认浏览器 → App Paths → 常见安装路径）。
        /// </summary>
        public string LauncherBrowser { get; set; } = "";
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

    /// <summary>机位标识（**不修改学生机系统壁纸**，只用于断网遮罩等界面显示）。</summary>
    public class SiteSettings
    {
        /// <summary>在断网遮罩上显示机器编号（取计算机名后 N 位）。</summary>
        public bool ShowMachineNumber { get; set; } = true;
        public int MachineNumberChars { get; set; } = 6;
    }

    public class RegistryAclSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>被加固的注册表键（拒绝普通用户写入）。</summary>
        public List<string> Keys { get; set; } = new List<string>
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\LabGuard",
            // 课堂软件自己的键（学生用破解工具常改这里的频道/自动登录参数）。
            // 机器上没装这个软件时该键不存在 → 加固会自动跳过，不会凭空创建。
            @"HKEY_LOCAL_MACHINE\SOFTWARE\TopDomain\e-Learning Class\Student"
        };
        /// <summary>
        /// 连管理员也不许写这些键（等价原版 regini 的"锁成只读"配方；SYSTEM 保留完全控制，服务仍可回写）。
        /// 默认关：打开后老师用管理员账号也改不了这些键，需要先关掉本开关或停服务。
        /// </summary>
        public bool DenyAdminsWrite { get; set; } = false;
    }

    public class WatchdogSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>程序文件被删除/被改时，从"系统级备份副本"自动恢复（默认开）。</summary>
        public bool RestoreMissingFiles { get; set; } = true;
        /// <summary>心跳上报地址（可选）：填了之后每 60 秒把本机状态 POST 给教师机上的监视器。</summary>
        public string ReportUrl { get; set; } = "";
        /// <summary>服务异常退出后自动重启电脑（sc failure … actions=reboot）。</summary>
        public bool RebootOnServiceFailure { get; set; } = false;
        /// <summary>代理进程被结束后自动重新拉起的间隔（秒）。</summary>
        public int AgentRestartSeconds { get; set; } = 10;
        /// <summary>隐藏安装目录（Hidden+System 属性；学生不易发现，可在设置里关闭）。</summary>
        public bool HideInstallDir { get; set; } = true;
        /// <summary>关键文件校验失败时是否进入锁定。</summary>
        public bool LockOnIntegrityFailure { get; set; } = true;
    }

    /// <summary>
    /// 进程防拆加固（对付"学生从任务管理器一键杀掉 LabGuard"）。
    /// 分四层，任意一层生效都能让"杀进程"变成无用功，四层一起开才叫加固：
    /// ① 进程 DACL 硬化（标准用户点不动）→ ② 秒级复活（管理员杀掉也立刻回来）
    /// → ③ 兜底计划任务（服务被停/被删也能自愈）→ ④ 反复被杀告警（老师能看见谁在搞事）。
    /// 边界写在 docs/07：管理员身份的学生**仍杀得掉**，本组不假装能做到驱动级保护。
    /// </summary>
    public class AntiTamperSettings
    {
        public bool Enabled { get; set; } = true;
        /// <summary>
        /// ① 给小助手/服务进程套 DACL：只有 SYSTEM 与管理员能终止，当前用户只剩"查询"权。
        /// 标准学生在任务管理器点「结束任务」会收到「拒绝访问」。
        /// </summary>
        public bool HardenProcessDacl { get; set; } = true;
        /// <summary>
        /// ② 秒级复活：服务用进程句柄等待（WaitForSingleObject），小助手一退出就立刻重拉，
        /// 不再等 <c>Watchdog.AgentRestartSeconds</c> 的轮询间隔。
        /// </summary>
        public bool FastRestart { get; set; } = true;
        /// <summary>
        /// ③ 兜底计划任务（SYSTEM，每分钟）：服务不在就 net start，小助手不在就拉起。
        /// 对付"学生把服务停掉/删掉"——服务和代理同时没了也还有这一条。
        /// </summary>
        public bool ScheduledTaskGuard { get; set; } = true;
        /// <summary>④ 反复被杀时升级告警并记入 tamper 标记（老师能从日志/心跳看到）。</summary>
        public bool AlertOnRepeatedKill { get; set; } = true;
        /// <summary>④ 的阈值：这么多分钟内被杀这么多次才算"有人在搞事"。</summary>
        public int RepeatKillWindowMinutes { get; set; } = 5;
        /// <summary>④ 的次数阈值。</summary>
        public int RepeatKillCount { get; set; } = 3;
        /// <summary>
        /// 顺手把常见的"杀进程工具"（Process Hacker / SystemInformer / taskkill 等）纳入违规软件拦截。
        /// 用现有的「进程/内核/注册表工具」清单，不新增一套匹配逻辑。
        /// </summary>
        public bool BlockKillTools { get; set; } = true;
        /// <summary>
        /// 「老师暂停监控」期间**仍然守护进程**（小助手被杀照样拉起，只是以"已暂停"形态运行、不启用策略）。
        /// 真机事故教训（2026-10-08）：老师手势解锁断网遮罩 → 写入 paused.flag →
        /// 服务/兜底任务三处都因 IsPaused 直接 return → 学生杀掉小助手后**永远没人拉起**，
        /// 而且托盘没了、老师点不到"启动监控"，只能手工跑设置程序 → 死锁。
        /// 暂停的语义应当是"停止管控策略"，不是"允许别人杀掉我的进程"。默认开。
        /// </summary>
        public bool KeepAliveWhenPaused { get; set; } = true;
    }
}
