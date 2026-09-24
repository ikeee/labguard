using System.Collections.Generic;

namespace LabGuard.Core.Data
{
    /// <summary>
    /// 内置拦截清单（**全部可在设置里开关，也可自行增删**）。
    ///
    /// 说明：
    /// · 工具/软件名属于客观事实，这里按"机房上课时容易被用来脱离课堂管控、或与教学无关"分类整理；
    /// · 域名清单是**常见公共站点**（网盘/文件分享/在线小游戏）的整理结果，不是任何第三方产品的私有数据，
    ///   你可以按学校实际教学需要随意增删（例如把要用到的站点去掉）；
    /// · 任何清单都不应该被当作"黑白名单的权威来源"——请以自己机房的实际需求为准。
    /// </summary>
    public static class DefaultBlocklists
    {
        /// <summary>脱离课堂管控类工具（进程名或窗口标题包含即命中）。</summary>
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

        /// <summary>虚拟桌面类（学生用它脱离课堂广播）。</summary>
        public static readonly string[] VirtualDesktop =
        {
            "Dexpot", "DexpotProPortable", "Deskman", "iDesktop", "YtMDesk", "Desktops",
            "Wise Desktop", "VDesktop", "MagicDesktop", "MultiDesk", "小宝虚拟桌面",
            "MultiDeskTop应用程序"
        };

        /// <summary>杀毒 / 安全管家类（会拦截课堂软件与管控本身，按需关闭）。</summary>
        public static readonly string[] AntiVirus =
        {
            "360safe", "HipsTray", "HipsDaemon", "QQPCTray", "QQPCRTP", "360taskmgr",
            "QQPCNetFlow", "360netman", "瑞星防火墙", "火绒剑", "rfwmain", "RavMonD", "wsctrl"
        };

        /// <summary>解压缩软件（防止学生解压下载来的破解包）。</summary>
        public static readonly string[] Archivers =
        {
            "winrar", "WinRAR", "7zFM", "7z", "Bandizip", "360zip", "haozip", "2345haozip",
            "WinZip", "WinZip32", "PeaZip", "好压"
        };

        /// <summary>系统工具（按窗口标题匹配）。</summary>
        public static readonly string[] SystemToolTitles =
        {
            "任务管理器", "Windows 命令处理程序", "Windows Command Processor", "Windows PowerShell",
            "注册表编辑器", "Microsoft 管理控制台", "本地组策略编辑器", "资源监视器"
        };

        /// <summary>小游戏（进程名）。</summary>
        public static readonly string[] Games =
        {
            "winmine", "Minesweeper", "solitaire", "Solitaire", "SpiderSolitaire", "FreeCell",
            "Hearts", "Chess", "Mahjong", "PurblePlace", "bckgzm", "chkrzm", "shvlzm", "sidebar"
        };

        /// <summary>需要直接禁用的程序（IFEO：把 Debugger 指向空）。</summary>
        public static string[] GamesIfeo => new[]
        {
            "sidebar.exe", "Chess.exe", "FreeCell.exe", "Hearts.exe", "Minesweeper.exe",
            "PurblePlace.exe", "Mahjong.exe", "SpiderSolitaire.exe", "bckgzm.exe", "chkrzm.exe",
            "shvlzm.exe", "Solitaire.exe", "winmine", "Magnify.exe"
        };

        /// <summary>高危系统命令（IFEO）：防止学生用它结束课堂软件/改路由绕过域名过滤。</summary>
        public static readonly string[] BlockedCommandsIfeo = { "taskkill.exe", "ntsd.exe", "tasklist.exe", "route.exe" };

        /// <summary>
        /// 域名黑名单（**可选功能，默认关闭**）：网盘 / 文件分享 / 在线小游戏等常见站点。
        /// 仅在你没有集中 DNS 过滤、且希望在本机做域名拦截时使用；可自由增删。
        /// </summary>
        public static readonly string[] Domains =
        {
            // —— 网盘 / 文件分享 ——
            "pan.baidu.com", "www.alipan.com", "www.aliyundrive.com", "www.123pan.com",
            "pan.quark.cn", "www.weiyun.com", "115.com", "cloud.189.cn", "pan.xunlei.com",
            "caiyun.feixin.10086.cn", "www.icloud.com", "www.onedrive.live.com",
            "lanzou.com", "lanzoux.com", "lanzoui.com", "lanzoub.com", "lanzoue.com",
            "lanzoup.com", "lanzous.com", "lanzout.com", "lanzouy.com", "www.ilanzou.com",
            // —— 软件下载站 ——
            "download.csdn.net", "www.cr173.com", "www.pc6.com", "www.crsky.com",
            // —— 在线小游戏 ——
            "poki.com", "poki.cn", "about.poki.com", "digdig.io", "www.crazygames.com",
            "www.miniclip.com", "www.4399.com", "www.7k7k.com"
        };

        /// <summary>常见课堂软件客户端（用于自动探测；填不填都不影响其它功能）。</summary>
        public static readonly KeyValuePair<string, string>[] ClassroomHints =
        {
            new KeyValuePair<string, string>("REDAgent.exe", @"C:\Program Files (x86)\3000soft\Red Spider\REDAgent.exe"),
            new KeyValuePair<string, string>("StudentMain.exe", @"C:\Program Files (x86)\TopDomain\e-Learning Class\Student\StudentMain.exe"),
            new KeyValuePair<string, string>("ClassMangerApp.exe", @"E:\Program Files (x86)\ClassManager\ClassMangerApp.exe")
        };
    }
}
