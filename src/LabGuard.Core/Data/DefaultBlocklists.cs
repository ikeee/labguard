using System.Collections.Generic;

namespace LabGuard.Core.Data
{
    /// <summary>
    /// 内置拦截清单。前 5 组数据**直接来自原版字符串表还原结果**（见 docs/01-原理分析），
    /// 域名清单来自原版安装目录的 hmd_local.txt。
    /// </summary>
    public static class DefaultBlocklists
    {
        /// <summary>破解/脱控类工具（进程名或窗口标题包含即命中）。</summary>
        public static readonly string[] ClassroomCrack =
        {
            "极域杀手", "极域墓地制造器", "极域杀死", "翘课", "学生机房Hack助手", "再见极域",
            "KillControl", "去除控制", "MsgFlood", "极域工具包", "Astrum For Online Classroom",
            "JiYu Trainer", "FuckMythware", "Hello Teacher", "JIYUPLUS", "极域Tool", "微机课救星",
            "极域X", "UnMythware", "DzjsTools", "SkiesKiller", "极域电子教室克星", "掌控课堂",
            "课堂狂欢器", "夕颜若雪工具箱", "吾爱破解", "教室终结者", "NoTopDomain"
        };

        /// <summary>进程/内核/注册表类工具。</summary>
        public static readonly string[] ProcessTools =
        {
            "PC Hunter", "PCHunter", "IceSword", "Wsyscheck", "SnipeSword", "Process Explorer",
            "Process Hacker", "Process Lasso", "Process Master", "Process-X", "ProcessOVER",
            "PrcView", "Process Viewer", "process manager", "PowerTool", "Windows Kernel Explorer",
            "FTCleaner", "OpenArk", "Procmon", "超级进程王", "Registry Workshop", "系统配置实用程序",
            "资源和性能监视器", "Special Setting Program", "BITS管理实用程序", "狡兔二窟",
            "procexp", "procexp64"
        };

        /// <summary>虚拟桌面类（学生用它脱离电子教室控制）。</summary>
        public static readonly string[] VirtualDesktop =
        {
            "Dexpot", "DexpotProPortable", "Deskman", "iDesktop", "YtMDesk", "Desktops",
            "Wise Desktop", "VDesktop", "MagicDesktop", "MultiDesk", "小宝虚拟桌面",
            "MultiDeskTop应用程序"
        };

        /// <summary>杀毒/安全管家类（会拦截电子教室与管控，对应原版 shadu_jianche）。</summary>
        public static readonly string[] AntiVirus =
        {
            "360safe", "360safe.exe", "HipsTray", "HipsDaemon", "QQPCTray", "QQPCRTP",
            "360taskmgr", "QQPCNetFlow", "360netman", "iu杀毒", "瑞星防火墙", "火绒剑",
            "rfwmain", "RavMonD", "wsctrl"
        };

        /// <summary>解压软件（防止学生解压下载来的破解包；原版用重复字母规避特征）。</summary>
        public static readonly string[] Archivers =
        {
            "winrar", "winrarrr", "wzmainnn", "bandizippp", "7zFM", "7zFMMM", "360zippp",
            "360zip", "haozip", "2345haozip", "WinZip", "PeaZip"
        };

        /// <summary>系统工具（按窗口标题匹配，对应原版 gaowei / cmd_jianche / zcb）。</summary>
        public static readonly string[] SystemToolTitles =
        {
            "任务管理器", "Windows 命令处理程序", "Windows Command Processor", "Windows PowerShell",
            "注册表编辑器", "Microsoft 管理控制台", "本地组策略编辑器", "资源监视器"
        };

        /// <summary>小游戏（进程名；原版同时用 IFEO 的 Debugger=null 直接禁用）。</summary>
        public static readonly string[] Games =
        {
            "winmine", "winmine.exe", "Minesweeper", "solitaire", "solitaire.exe", "Solitaire",
            "SpiderSolitaire", "FreeCell", "Hearts", "Chess", "Mahjong", "PurblePlace",
            "bckgzm", "chkrzm", "shvlzm", "sidebar"
        };

        public static string[] GamesIfeo => new[]
        {
            "sidebar.exe", "Chess.exe", "FreeCell.exe", "Hearts.exe", "Minesweeper.exe",
            "PurblePlace.exe", "Mahjong.exe", "SpiderSolitaire.exe", "bckgzm.exe", "chkrzm.exe",
            "shvlzm.exe", "Solitaire.exe", "winmine", "Magnify.exe"
        };

        /// <summary>IFEO 直封的高危系统命令（原版做法）。</summary>
        public static readonly string[] BlockedCommandsIfeo = { "taskkill.exe", "ntsd.exe", "tasklist.exe", "route.exe" };

        /// <summary>浏览器下载/网盘/游戏站点黑名单（原版 hmd_local.txt 全量）。</summary>
        public static readonly string[] Domains =
        {
            "www.lanzou.com", "lanzoux.com", "lanzoui.com", "www.lanzoui.com", "lanzoub.com",
            "wwu.lanzoul.com", "wws.lanzous.com", "wwxu.lanzouu.com", "wwt.lanzoub.com",
            "wwtc.lanzouq.com", "wwp.lanzoup.com", "www.ilanzou.com", "wwn.lanzoul.com",
            "wsyg.lanzouw.com", "hvhlz-001.lanzouo.com", "wwf.lanzouv.com", "www.lanzoux.com",
            "wwyk.lanzoue.com", "ww.lanzoui.com", "www.lanzous.com", "wwgd.lanzoul.com",
            "wwr.lanzoui.com", "lanzoup.com", "pan.lanzou.com", "download.lanzou.com",
            "lanzous.com", "lanzouy.com", "lanzout.com", "gitee.com", "mg-class.github.io",
            "www.123912.com", "www.123684.com", "pan.baidu.com", "www.123pan.com",
            "pan.huang1111.cn", "www.alipan.com", "www.weiyun.com", "github.com",
            "dzjszjz.nkxingxh.top", "nkxingxh.lanzoul.com", "www.astrumstudio.top",
            "astrum.lanzoue.com", "download.csdn.net", "www.cr173.com", "pan.quark.cn",
            "www.aliyundrive.com", "cloud.189.cn", "caiyun.feixin.10086.cn",
            "www.onedrive.live.com", "www.icloud.com", "115.com", "pan.sysre.cn",
            "pan.xunlei.com", "dream.superboy.xin", "dzjstools.top", "131129.top",
            "wwil.lanzoul.com", "blog.csdn.net", "space.bilibili.com", "6e6nra.lanzoul.com",
            "one.nkxingxh.top", "blog.nkxingxh.top", "pjdzjs.us.kg", "mcr130102.lanzoul.com",
            "kpjcq.lanzout.com", "stdio.run", "poki.cn", "poki.com", "poki.homes",
            "poki.us.com", "poki.co.com", "poki.us.org", "poki.ac", "about.poki.com",
            "digdig.io"
        };

        /// <summary>常见电子教室客户端（用于自动探测）。</summary>
        public static readonly KeyValuePair<string, string>[] ClassroomHints =
        {
            new KeyValuePair<string, string>("REDAgent.exe", @"C:\Program Files (x86)\3000soft\Red Spider\REDAgent.exe"),
            new KeyValuePair<string, string>("StudentMain.exe", @"C:\Program Files (x86)\TopDomain\e-Learning Class\Student\StudentMain.exe"),
            new KeyValuePair<string, string>("ClassMangerApp.exe", @"E:\Program Files (x86)\ClassManager\ClassMangerApp.exe")
        };
    }
}
