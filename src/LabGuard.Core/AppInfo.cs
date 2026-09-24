namespace LabGuard.Core
{
    /// <summary>程序身份与版本（对外显示统一走这里）。</summary>
    public static class AppInfo
    {
        /// <summary>产品名（界面/日志显示用）。</summary>
        public const string ProductName = "机房管理助手（复刻）";
        public const string ProductNameEn = "LabGuard";
        /// <summary>当前版本（与 git tag v0.01 对应）。</summary>
        public const string Version = "0.01";
        public const string RepoUrl = "https://github.com/ikeee/labguard";
    }
}
