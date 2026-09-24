using System;
using System.Collections.Generic;
using LabGuard.Core.Config;

namespace LabGuard.Core.Settings
{
    public enum FieldKind { Bool, Choice, Number, Text, Path, List }

    /// <summary>
    /// 一个配置项 = 界面上的一个控件。
    /// <see cref="Path"/> 必须与 GuardConfig 的属性路径完全一致，供"配置覆盖自检"核对（见 SelfTest）。
    /// </summary>
    public sealed class FieldSpec
    {
        public string Section { get; set; }
        public string Path { get; set; }
        public string Label { get; set; }
        public string Hint { get; set; }
        public FieldKind Kind { get; set; }
        public Func<GuardConfig, object> Get { get; set; }
        public Action<GuardConfig, object> Set { get; set; }
        /// <summary>Choice：显示文本</summary>
        public string[] Choices { get; set; }
        /// <summary>Choice：对应存储值（字符串）</summary>
        public string[] Values { get; set; }
        public decimal Min { get; set; } = 0;
        public decimal Max { get; set; } = 100000;
        /// <summary>Path：文件对话框筛选器</summary>
        public string Filter { get; set; }
        /// <summary>危险项：界面标红提醒（如"关机/重启"）</summary>
        public bool Dangerous { get; set; }
    }

    /// <summary>
    /// 全部可配置项目录。**凡是 GuardConfig 里的叶子属性（除版本号/口令）都必须在这里出现**，
    /// 否则自检 `Test_SettingsCoverage` 会失败——保证"每个功能都有开关、且没有藏在代码里的隐藏开关"。
    /// </summary>
    public static class SettingsCatalog
    {
        /// <summary>不放进"开关目录"的特殊项（口令有独立分组；Version 为内部版本号）。</summary>
        public static readonly string[] SpecialPaths = { "Version", "PasswordHash" };

        public static List<FieldSpec> All()
        {
            var list = new List<FieldSpec>();

            // ============================================================ 0 总体
            list.Add(Bool("0、总体", "Enabled", "启用监控（总开关）",
                "关掉后所有模块停止，系统设置会被还原（等于「老师暂停」状态）", c => c.Enabled, (c, v) => c.Enabled = (bool)v));
            list.Add(Number("0、总体", "StartDelaySeconds", "开机后延迟开始检测（秒）",
                "原版经验值 120 秒：等电子教室/网络稳定后再判定，避免开机误报", 0, 3600,
                c => c.StartDelaySeconds, (c, v) => c.StartDelaySeconds = (int)v));
            list.Add(Number("0、总体", "ResumeAfterMinutes", "暂停后自动恢复监控（分钟）",
                "0 = 不自动恢复；例如填 45 = 老师暂停后 45 分钟自动重新开启", 0, 1440,
                c => c.ResumeAfterMinutes, (c, v) => c.ResumeAfterMinutes = (int)v));

            // ============================================================ 1 电子教室
            list.Add(Bool("1、电子教室保护", "Classroom.Enabled", "启用电子教室保护（本组总开关）", null,
                c => c.Classroom.Enabled, (c, v) => c.Classroom.Enabled = (bool)v));
            list.Add(Path("1、电子教室保护", "Classroom.MainExecutable", "电子教室学生端程序路径",
                "极域 StudentMain.exe / 红蜘蛛 REDAgent.exe / 锐捷 ClassMangerApp.exe；留空 = 自动探测",
                "exe文件 |*.exe", c => c.Classroom.MainExecutable, (c, v) => c.Classroom.MainExecutable = (string)v));
            list.Add(List("1、电子教室保护", "Classroom.ProcessNames", "学生端进程名（一行一个）",
                "找不到进程时按这些名字找（.exe 可省略）", c => c.Classroom.ProcessNames, (c, v) => c.Classroom.ProcessNames = (List<string>)v));
            list.Add(List("1、电子教室保护", "Classroom.RequiredServices", "必须运行的服务（一行一个）",
                "极域默认 TopDomainClient / TopDomainClientHelper；留空 = 不检查服务",
                c => c.Classroom.RequiredServices, (c, v) => c.Classroom.RequiredServices = (List<string>)v));
            list.Add(Bool("1、电子教室保护", "Classroom.ResumeWhenSuspended", "被挂起时强制恢复",
                "对应原版「电子教室程序被暂停！」的处置（NtResumeProcess）",
                c => c.Classroom.ResumeWhenSuspended, (c, v) => c.Classroom.ResumeWhenSuspended = (bool)v));
            list.Add(Bool("1、电子教室保护", "Classroom.RelaunchWhenKilled", "被结束进程时自动重新拉起", null,
                c => c.Classroom.RelaunchWhenKilled, (c, v) => c.Classroom.RelaunchWhenKilled = (bool)v));
            list.Add(Bool("1、电子教室保护", "Classroom.GuardELearningParameters", "守护极域频道/自动登录参数",
                "监控 SOFTWARE\\TopDomain\\e-Learning Class\\Student（ChannelId / AutoLogin）",
                c => c.Classroom.GuardELearningParameters, (c, v) => c.Classroom.GuardELearningParameters = (bool)v));

            // ============================================================ 2 网络与防火墙
            list.Add(Bool("2、网络与防火墙", "Network.Enabled", "启用网络守护（本组总开关）", null,
                c => c.Network.Enabled, (c, v) => c.Network.Enabled = (bool)v));
            list.Add(Bool("2、网络与防火墙", "Network.DetectDisconnected", "检测拔网线 / 禁用网卡",
                "网卡变 Down / IP 清空 / 0.0.0.0 / 169.254.* 持续 20 秒才判定",
                c => c.Network.DetectDisconnected, (c, v) => c.Network.DetectDisconnected = (bool)v));
            list.Add(List("2、网络与防火墙", "Network.WatchedInterfaces", "只监控这些网卡（一行一个，留空=自动）",
                "一体机机房建议只填有线网卡名（如「以太网」）；自动 = 选有默认网关的网卡",
                c => c.Network.WatchedInterfaces, (c, v) => c.Network.WatchedInterfaces = (List<string>)v));
            list.Add(Bool("2、网络与防火墙", "Network.WatchWiredOnly", "只监视有线网卡（无线/热点不参与断网判定）",
                "默认开：一体机机房专用——热点怎么开怎么关都不会被误判成拔网线；若本机没有有线网卡会自动退回自动选择并记日志",
                c => c.Network.WatchWiredOnly, (c, v) => c.Network.WatchWiredOnly = (bool)v));
            list.Add(List("2、网络与防火墙", "Network.ExcludedInterfaces", "忽略的网卡关键字（一行一个）",
                "默认已忽略 Wi-Fi Direct / 移动热点 / Hyper-V / VMware / 蓝牙等；做热点的机器别把无线加进监控",
                c => c.Network.ExcludedInterfaces, (c, v) => c.Network.ExcludedInterfaces = (List<string>)v));
            list.Add(Bool("2、网络与防火墙", "Network.DisconnectRequireAllDown", "要全部判据网卡断开才算断网",
                "推荐开启：有线拔了但无线还在 = 机器仍有网，不该弹遮罩；关掉则「任意一个断开就判断网」",
                c => c.Network.DisconnectRequireAllDown, (c, v) => c.Network.DisconnectRequireAllDown = (bool)v));
            list.Add(Bool("2、网络与防火墙", "Network.RestoreOriginalIp", "检测到改 IP 时还原原 IP",
                "按启动时记录的基线用 netsh 改回（DHCP 的机器改回 source=dhcp）",
                c => c.Network.RestoreOriginalIp, (c, v) => c.Network.RestoreOriginalIp = (bool)v));
            list.Add(Bool("2、网络与防火墙", "Network.ForceFirewallOff", "发现防火墙「阻止所有传入连接」时强制关闭",
                "原版行为：netsh advfirewall set allprofiles state off",
                c => c.Network.ForceFirewallOff, (c, v) => c.Network.ForceFirewallOff = (bool)v));
            list.Add(Bool("2、网络与防火墙", "Network.LockOnViolation", "网络违规时全屏锁定",
                "拔网线/改 IP 时锁屏（原版默认更狠；关闭则只提示 + 自动恢复）",
                c => c.Network.LockOnViolation, (c, v) => c.Network.LockOnViolation = (bool)v));
            list.Add(Bool("2、网络与防火墙", "Network.ShutdownOnViolation", "网络违规时关机/重启【危险】",
                "仅在确认要「零容忍」时打开；会让一节普通的网线松动变成关机",
                c => c.Network.ShutdownOnViolation, (c, v) => c.Network.ShutdownOnViolation = (bool)v, dangerous: true));
            list.Add(Bool("2、网络与防火墙", "Network.DisconnectMask", "断网时全屏遮罩（屏保式）",
                "插回网线自动消失、不需要老师；老师连续按 5 次 Esc 输入密码可解除（解除后监控暂停）",
                c => c.Network.DisconnectMask, (c, v) => c.Network.DisconnectMask = (bool)v));
            list.Add(Choice("2、网络与防火墙", "Network.DisconnectMaskBackground", "遮罩背景",
                "随机壁纸 = 从安装目录 wallpaper 里随机选一张；纯色 = 深色提示页",
                c => c.Network.DisconnectMaskBackground, (c, v) => c.Network.DisconnectMaskBackground = (string)v,
                new[] { "随机壁纸", "纯色提示页" }, new[] { "Wallpaper", "Plain" }));
            list.Add(Bool("2、网络与防火墙", "Network.DisconnectMaskShowElapsed", "遮罩上显示已断开时长",
                null, c => c.Network.DisconnectMaskShowElapsed,
                (c, v) => c.Network.DisconnectMaskShowElapsed = (bool)v));
            list.Add(Number("2、网络与防火墙", "Network.DisconnectSoundAfterSeconds", "断网多少秒后响鸣（0=不响）",
                "建议 60 秒；响鸣是「提醒」不是「惩罚」，慎用长时间",
                0, 3600, c => c.Network.DisconnectSoundAfterSeconds,
                (c, v) => c.Network.DisconnectSoundAfterSeconds = (int)v));
            list.Add(Number("2、网络与防火墙", "Network.DisconnectSoundTimes", "响鸣次数上限",
                "默认 3 次；上限小可以避免「学生拔线制造噪音」变成新玩法", 1, 60,
                c => c.Network.DisconnectSoundTimes, (c, v) => c.Network.DisconnectSoundTimes = (int)v));
            list.Add(Number("2、网络与防火墙", "Network.DisconnectSoundIntervalMs", "响鸣间隔（毫秒）",
                "默认 700 毫秒（嘀嘀嘀）", 200, 5000,
                c => c.Network.DisconnectSoundIntervalMs, (c, v) => c.Network.DisconnectSoundIntervalMs = (int)v));

            // ============================================================ 3 集中 DNS
            list.Add(Bool("3、集中 DNS（上网过滤主防线）", "Network.EnforceDns", "锁定学生机 DNS",
                "学生改成 8.8.8.8/114 等即强制改回；未填 DNS 地址时本项无效",
                c => c.Network.EnforceDns, (c, v) => c.Network.EnforceDns = (bool)v));
            list.Add(List("3、集中 DNS（上网过滤主防线）", "Network.DnsServers", "DNS 服务器（一行一个）",
                "填教师机 IP（如 192.168.1.10）；第一行为主 DNS。留空 = 不锁定（适合 DHCP 统一下发）",
                c => c.Network.DnsServers, (c, v) => c.Network.DnsServers = (List<string>)v));
            list.Add(Bool("3、集中 DNS（上网过滤主防线）", "Network.BlockDoh", "关闭浏览器/系统加密 DNS（DoH）",
                "Chrome/Edge/Firefox + Win11 系统级；不关的话学生一次点击即可绕过 AdGuard",
                c => c.Network.BlockDoh, (c, v) => c.Network.BlockDoh = (bool)v));
            list.Add(Bool("3、集中 DNS（上网过滤主防线）", "Network.DnsLockOnlyWatchedInterfaces",
                "DNS 只锁在上网网卡上（不动热点/无线的 DNS）",
                "推荐开启：否则热点客户端（STEAM 课的机器人/平板）会被塞进教师机 DNS",
                c => c.Network.DnsLockOnlyWatchedInterfaces, (c, v) => c.Network.DnsLockOnlyWatchedInterfaces = (bool)v));

            // ============================================================ 4 违规软件
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.Enabled", "启用违规软件拦截（本组总开关）", null,
                c => c.ProcessBlock.Enabled, (c, v) => c.ProcessBlock.Enabled = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockClassroomCrack", "拦截破解/脱控工具",
                "极域杀手/翘课/KillControl 等 28 条；同时用 IFEO 封 taskkill/ntsd/tasklist/route",
                c => c.ProcessBlock.BlockClassroomCrack, (c, v) => c.ProcessBlock.BlockClassroomCrack = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockProcessTools", "拦截进程/内核工具",
                "Process Hacker / PC Hunter / IceSword 等 28 条", c => c.ProcessBlock.BlockProcessTools,
                (c, v) => c.ProcessBlock.BlockProcessTools = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockVirtualDesktop", "拦截虚拟桌面软件",
                "Dexpot / MultiDesk 等（学生用虚拟桌面脱离电子教室）", c => c.ProcessBlock.BlockVirtualDesktop,
                (c, v) => c.ProcessBlock.BlockVirtualDesktop = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockAntiVirus", "拦截杀毒/安全管家",
                "360safe / HipsTray(火绒) / QQPCTray 等（对应原版 shadu_jianche）",
                c => c.ProcessBlock.BlockAntiVirus, (c, v) => c.ProcessBlock.BlockAntiVirus = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockArchivers", "拦截解压软件",
                "防止学生解压下载来的破解包", c => c.ProcessBlock.BlockArchivers,
                (c, v) => c.ProcessBlock.BlockArchivers = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockTaskManager", "拦截任务管理器", null,
                c => c.ProcessBlock.BlockTaskManager, (c, v) => c.ProcessBlock.BlockTaskManager = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockRegedit", "拦截注册表编辑器（按进程）", null,
                c => c.ProcessBlock.BlockRegedit, (c, v) => c.ProcessBlock.BlockRegedit = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockCommandPrompt", "拦截命令提示符 / PowerShell", null,
                c => c.ProcessBlock.BlockCommandPrompt, (c, v) => c.ProcessBlock.BlockCommandPrompt = (bool)v));
            list.Add(Bool("4、违规软件拦截", "ProcessBlock.BlockGames", "拦截小游戏 + 关浏览器小游戏",
                "扫雷/纸牌/拼图板 + IFEO 禁用；同时关 Chrome 小恐龙与 Edge 冲浪",
                c => c.ProcessBlock.BlockGames, (c, v) => c.ProcessBlock.BlockGames = (bool)v));
            list.Add(Choice("4、违规软件拦截", "ProcessBlock.Action", "发现违规软件时怎么处理",
                null, c => c.ProcessBlock.Action, (c, v) => c.ProcessBlock.Action = (string)v,
                new[] { "只提示（先观察）", "结束该进程（推荐）", "锁屏", "关机/重启【危险】" },
                new[] { "Notify", "Kill", "Lock", "Reboot" }));
            list.Add(List("4、违规软件拦截", "ProcessBlock.ExtraKeywords", "额外拦截关键词（一行一个）",
                "按进程名或窗口标题包含匹配，用于你们学校特有的软件",
                c => c.ProcessBlock.ExtraKeywords, (c, v) => c.ProcessBlock.ExtraKeywords = (List<string>)v));

            // ============================================================ 5 新文件监控
            list.Add(Bool("5、新文件/下载监控", "FileCreation.Enabled", "启用新文件监控（本组总开关）", null,
                c => c.FileCreation.Enabled, (c, v) => c.FileCreation.Enabled = (bool)v));
            list.Add(Choice("5、新文件/下载监控", "FileCreation.Mode", "监控强度",
                "mpython、考试软件、3D One 需要「仅 C++」或「全部允许」",
                c => c.FileCreation.Mode, (c, v) => c.FileCreation.Mode = (string)v,
                new[] { "全部允许", "仅 C++ 可生成新 exe（推荐）", "全部禁止" },
                new[] { "AllowAll", "CppOnly", "DenyAll" }));
            list.Add(Choice("5、新文件/下载监控", "FileCreation.Action", "发现高危新文件时",
                null, c => c.FileCreation.Action, (c, v) => c.FileCreation.Action = (string)v,
                new[] { "删除（推荐）", "只提示不删除" }, new[] { "Delete", "Notify" }));
            list.Add(List("5、新文件/下载监控", "FileCreation.WatchPaths", "监控目录（一行一个）",
                "支持 %USERPROFILE% 变量；根目录（如 D:\\）只监控根层不递归",
                c => c.FileCreation.WatchPaths, (c, v) => c.FileCreation.WatchPaths = (List<string>)v));
            list.Add(List("5、新文件/下载监控", "FileCreation.HighRiskExtensions", "高危扩展名（一行一个）",
                null, c => c.FileCreation.HighRiskExtensions,
                (c, v) => c.FileCreation.HighRiskExtensions = (List<string>)v));
            list.Add(List("5、新文件/下载监控", "FileCreation.AllowedExtensions", "放行扩展名（一行一个）",
                "白名单优先：这些扩展名即使在高危列表里也不处理",
                c => c.FileCreation.AllowedExtensions, (c, v) => c.FileCreation.AllowedExtensions = (List<string>)v));

            // ============================================================ 6 USB
            list.Add(Bool("6、USB存储设备", "Usb.Enabled", "启用 USB 管控（本组总开关）", null,
                c => c.Usb.Enabled, (c, v) => c.Usb.Enabled = (bool)v));
            list.Add(Bool("6、USB存储设备", "Usb.AllowStorage", "允许使用 USB 存储设备",
                "取消勾选 = 禁用 usbstor 驱动（U盘/移动硬盘不可用）",
                c => c.Usb.AllowStorage, (c, v) => c.Usb.AllowStorage = (bool)v));
            list.Add(Bool("6、USB存储设备", "Usb.KeepHidDevices", "保留 USB 键鼠 / 机器人等外设",
                "只禁存储类设备、不禁 USB 本身（机器人、3D 打印机等走非存储协议）",
                c => c.Usb.KeepHidDevices, (c, v) => c.Usb.KeepHidDevices = (bool)v));

            // ============================================================ 7 hosts
            list.Add(Bool("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.Enabled", "启用 hosts 域名黑名单",
                "默认关闭：集中 DNS（教师机 AdGuard）已覆盖，且 hosts 可被本地改写",
                c => c.Hosts.Enabled, (c, v) => c.Hosts.Enabled = (bool)v));
            list.Add(Bool("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.ProtectFileAcl", "保护 hosts 文件（只读 + 内容守候）",
                "即使不用黑名单也建议开启：防学生写本地映射绕过集中 DNS",
                c => c.Hosts.ProtectFileAcl, (c, v) => c.Hosts.ProtectFileAcl = (bool)v));
            list.Add(Bool("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.Guard", "守护 hosts（被删/被改就恢复）", null,
                c => c.Hosts.Guard, (c, v) => c.Hosts.Guard = (bool)v));
            list.Add(Bool("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.HideHostsFile", "隐藏 hosts 文件（隐藏属性）",
                "原版做法；便于减少学生手工改文件的动机", c => c.Hosts.HideHostsFile,
                (c, v) => c.Hosts.HideHostsFile = (bool)v));
            list.Add(Bool("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.UseBuiltInDomains", "使用内置 75 条域名清单",
                "蓝奏/网盘/poki 等（来自原版 hmd_local.txt）", c => c.Hosts.UseBuiltInDomains,
                (c, v) => c.Hosts.UseBuiltInDomains = (bool)v));
            list.Add(Text("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.SinkAddress", "屏蔽目标地址",
                "127.0.0.1（默认）或 0.0.0.0（更彻底）", c => c.Hosts.SinkAddress,
                (c, v) => c.Hosts.SinkAddress = (string)v));
            list.Add(List("7、hosts 黑名单（有集中 DNS 时不需要）", "Hosts.ExtraDomains", "额外屏蔽域名（一行一个）", null,
                c => c.Hosts.ExtraDomains, (c, v) => c.Hosts.ExtraDomains = (List<string>)v));

            // ============================================================ 8 浏览器
            list.Add(Bool("8、浏览器管控", "Browser.Enabled", "启用浏览器策略（本组总开关）", null,
                c => c.Browser.Enabled, (c, v) => c.Browser.Enabled = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockDownloads", "禁止浏览器下载文件",
                "Chrome/Edge/Firefox/IE；学生仍可在浏览器复制图片再粘贴",
                c => c.Browser.BlockDownloads, (c, v) => c.Browser.BlockDownloads = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockSaveAs", "禁止另存为", null,
                c => c.Browser.BlockSaveAs, (c, v) => c.Browser.BlockSaveAs = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockDevTools", "禁止开发者工具", null,
                c => c.Browser.BlockDevTools, (c, v) => c.Browser.BlockDevTools = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockChromeDino", "关闭 Chrome 小恐龙", null,
                c => c.Browser.BlockChromeDino, (c, v) => c.Browser.BlockChromeDino = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockEdgeSurf", "关闭 Edge 冲浪游戏", null,
                c => c.Browser.BlockEdgeSurf, (c, v) => c.Browser.BlockEdgeSurf = (bool)v));
            list.Add(Bool("8、浏览器管控", "Browser.BlockIeDownload", "禁止 IE 下载", null,
                c => c.Browser.BlockIeDownload, (c, v) => c.Browser.BlockIeDownload = (bool)v));
            list.Add(Text("8、浏览器管控", "Browser.Homepage", "浏览器主页（留空 = 不改）",
                "建议填学校自己的导航页；原版是改成自家导航站变现",
                c => c.Browser.Homepage, (c, v) => c.Browser.Homepage = (string)v));
            list.Add(Bool("8、浏览器管控", "Browser.HijackShortcuts", "接管浏览器快捷方式（指向本程序上网入口）",
                "默认关闭；开启后桌面/开始菜单里的浏览器快捷方式会先经过 LabGuard.Launcher",
                c => c.Browser.HijackShortcuts, (c, v) => c.Browser.HijackShortcuts = (bool)v));

            // ============================================================ 9 任务栏/系统工具
            list.Add(Bool("9、任务栏与系统工具", "Shell.Enabled", "启用本组策略（总开关）", null,
                c => c.Shell.Enabled, (c, v) => c.Shell.Enabled = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.HideTaskView", "隐藏任务视图（虚拟桌面入口）", null,
                c => c.Shell.HideTaskView, (c, v) => c.Shell.HideTaskView = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.BlockWindowsKey", "屏蔽 Win 键（含 Win+Tab 等组合）", null,
                c => c.Shell.BlockWindowsKey, (c, v) => c.Shell.BlockWindowsKey = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.NoTrayContextMenu", "禁用任务栏右键菜单", null,
                c => c.Shell.NoTrayContextMenu, (c, v) => c.Shell.NoTrayContextMenu = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.DisableTaskManager", "禁用任务管理器（组策略）", null,
                c => c.Shell.DisableTaskManager, (c, v) => c.Shell.DisableTaskManager = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.DisableRegistryTools", "禁用注册表编辑器（组策略）", null,
                c => c.Shell.DisableRegistryTools, (c, v) => c.Shell.DisableRegistryTools = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.DisableCmd", "禁用命令提示符（组策略）", null,
                c => c.Shell.DisableCmd, (c, v) => c.Shell.DisableCmd = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.DisableLockWorkstation", "禁用锁定工作站（Win+L）", null,
                c => c.Shell.DisableLockWorkstation, (c, v) => c.Shell.DisableLockWorkstation = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.HideFileExtensions", "隐藏文件扩展名", null,
                c => c.Shell.HideFileExtensions, (c, v) => c.Shell.HideFileExtensions = (bool)v));
            list.Add(Bool("9、任务栏与系统工具", "Shell.NoFolderOptions", "禁用文件夹选项", null,
                c => c.Shell.NoFolderOptions, (c, v) => c.Shell.NoFolderOptions = (bool)v));

            // ============================================================ 10 安全模式
            list.Add(Bool("10、安全模式与启动菜单", "SafeMode.Enabled", "启用本组（总开关）", null,
                c => c.SafeMode.Enabled, (c, v) => c.SafeMode.Enabled = (bool)v));
            list.Add(Bool("10、安全模式与启动菜单", "SafeMode.RunInSafeMode", "安全模式下也继续管控【不推荐】",
                "默认关闭 = 安全模式不加载任何管控，给老师留后路（出问题/不再使用/彻底卸载都能进安全模式处理）",
                c => c.SafeMode.RunInSafeMode, (c, v) => c.SafeMode.RunInSafeMode = (bool)v, dangerous: true));
            list.Add(Bool("10、安全模式与启动菜单", "SafeMode.BlockNetworkSafeMode", "禁止「带网络连接的安全模式」",
                "删除 SafeBoot\\Network（删除前自动导出 .reg 备份，退出/卸载时还原）",
                c => c.SafeMode.BlockNetworkSafeMode, (c, v) => c.SafeMode.BlockNetworkSafeMode = (bool)v));
            list.Add(Bool("10、安全模式与启动菜单", "SafeMode.HideBootMenu", "隐藏开机启动菜单",
                "bcdedit /set {bootmgr} displaybootmenu no", c => c.SafeMode.HideBootMenu,
                (c, v) => c.SafeMode.HideBootMenu = (bool)v));

            // ============================================================ 11 壁纸
            list.Add(Bool("11、桌面壁纸与机器编号", "Wallpaper.Enabled", "启用统一壁纸", null,
                c => c.Wallpaper.Enabled, (c, v) => c.Wallpaper.Enabled = (bool)v));
            list.Add(Path("11、桌面壁纸与机器编号", "Wallpaper.Image", "壁纸图片（留空 = 用安装目录 wallpaper 里的）",
                null, "图片文件|*.bmp;*.jpg;*.jpeg;*.png", c => c.Wallpaper.Image,
                (c, v) => c.Wallpaper.Image = (string)v));
            list.Add(Bool("11、桌面壁纸与机器编号", "Wallpaper.ShowMachineNumber", "右上角显示机器编号", null,
                c => c.Wallpaper.ShowMachineNumber, (c, v) => c.Wallpaper.ShowMachineNumber = (bool)v));
            list.Add(Number("11、桌面壁纸与机器编号", "Wallpaper.MachineNumberChars", "编号取计算机名后几位",
                "默认 6 位（原版行为）", 1, 32, c => c.Wallpaper.MachineNumberChars,
                (c, v) => c.Wallpaper.MachineNumberChars = (int)v));
            list.Add(Bool("11、桌面壁纸与机器编号", "Wallpaper.GuardWallpaper", "壁纸被改时自动恢复", null,
                c => c.Wallpaper.GuardWallpaper, (c, v) => c.Wallpaper.GuardWallpaper = (bool)v));

            // ============================================================ 12 注册表加固
            list.Add(Bool("12、注册表权限加固", "RegistryAcl.Enabled", "启用注册表权限加固（总开关）",
                "把配置键设为「普通用户只读、管理员/SYSTEM 完全控制」", c => c.RegistryAcl.Enabled,
                (c, v) => c.RegistryAcl.Enabled = (bool)v));
            list.Add(List("12、注册表权限加固", "RegistryAcl.Keys", "加固的注册表键（一行一个）",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\LabGuard 等",
                c => c.RegistryAcl.Keys, (c, v) => c.RegistryAcl.Keys = (List<string>)v));

            // ============================================================ 13 互相守护
            list.Add(Bool("13、互相守护与自我保护", "Watchdog.Enabled", "启用守护（总开关）", null,
                c => c.Watchdog.Enabled, (c, v) => c.Watchdog.Enabled = (bool)v));
            list.Add(Bool("13、互相守护与自我保护", "Watchdog.RestoreMissingFiles", "程序文件被删/被改时自动恢复",
                "从系统级备份副本（%ProgramData%\\LabGuard\\payload，仅 SYSTEM/管理员可访问）恢复",
                c => c.Watchdog.RestoreMissingFiles, (c, v) => c.Watchdog.RestoreMissingFiles = (bool)v));
            list.Add(Text("13、互相守护与自我保护", "Watchdog.ReportUrl", "心跳上报地址（可选）",
                "填教师机监视器地址（如 http://192.168.1.10:8080/heartbeat），每 60 秒上报本机状态；留空=不上报",
                c => c.Watchdog.ReportUrl, (c, v) => c.Watchdog.ReportUrl = (string)v));
            list.Add(Number("13、互相守护与自我保护", "Watchdog.AgentRestartSeconds", "代理被结束后多少秒重新拉起",
                null, 5, 600, c => c.Watchdog.AgentRestartSeconds, (c, v) => c.Watchdog.AgentRestartSeconds = (int)v));
            list.Add(Bool("13、互相守护与自我保护", "Watchdog.RebootOnServiceFailure", "服务异常时重启电脑【危险】",
                "原版用 sc failure actions=reboot；默认关（只重启服务），打开前请想清楚",
                c => c.Watchdog.RebootOnServiceFailure, (c, v) => c.Watchdog.RebootOnServiceFailure = (bool)v, dangerous: true));
            list.Add(Bool("13、互相守护与自我保护", "Watchdog.LockOnIntegrityFailure", "关键文件被删改时锁定",
                "杀毒软件误删程序文件时会触发锁定（原版「缺文件就蓝屏锁定」）",
                c => c.Watchdog.LockOnIntegrityFailure, (c, v) => c.Watchdog.LockOnIntegrityFailure = (bool)v));

            return list;
        }

        // ------------------------------------------------------------------ 构造辅助
        private static FieldSpec Bool(string section, string path, string label, string hint,
            Func<GuardConfig, object> get, Action<GuardConfig, object> set, bool dangerous = false)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.Bool,
                Get = get, Set = set, Dangerous = dangerous };
        }

        private static FieldSpec Choice(string section, string path, string label, string hint,
            Func<GuardConfig, object> get, Action<GuardConfig, object> set, string[] choices, string[] values)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.Choice,
                Get = get, Set = set, Choices = choices, Values = values };
        }

        private static FieldSpec Number(string section, string path, string label, string hint,
            decimal min, decimal max, Func<GuardConfig, object> get, Action<GuardConfig, object> set)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.Number,
                Get = get, Set = set, Min = min, Max = max };
        }

        private static FieldSpec Text(string section, string path, string label, string hint,
            Func<GuardConfig, object> get, Action<GuardConfig, object> set)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.Text,
                Get = get, Set = set };
        }

        private static FieldSpec Path(string section, string path, string label, string hint, string filter,
            Func<GuardConfig, object> get, Action<GuardConfig, object> set)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.Path,
                Get = get, Set = set, Filter = filter };
        }

        private static FieldSpec List(string section, string path, string label, string hint,
            Func<GuardConfig, object> get, Action<GuardConfig, object> set)
        {
            return new FieldSpec { Section = section, Path = path, Label = label, Hint = hint, Kind = FieldKind.List,
                Get = get, Set = set };
        }
    }
}
