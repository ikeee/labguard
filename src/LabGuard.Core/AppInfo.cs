namespace LabGuard.Core
{
    /// <summary>程序身份与版本（对外显示统一走这里）。</summary>
    public static class AppInfo
    {
        /// <summary>产品名（界面/日志显示用）。</summary>
        public const string ProductName = "LabGuard";
        /// <summary>中文说明（用在"LabGuard · 机房管控工具"这类地方）。</summary>
        public const string ProductNameZh = "机房管控工具";
        /// <summary>桌面/开始菜单快捷方式显示名。</summary>
        public const string ShortcutName = "LabGuard（机房管控）";
        /// <summary>当前版本（与 git tag v0.01 对应）。</summary>
        public const string Version = "0.01";
        public const string RepoUrl = "https://github.com/ikeee/labguard";
    }
}
