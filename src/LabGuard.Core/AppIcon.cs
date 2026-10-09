using System.Drawing;
using System.IO;

namespace LabGuard.Core
{
    /// <summary>
    /// 程序图标（作者 GitHub 头像）。
    /// exe 的文件图标/任务栏/窗体左上角图标由 ApplicationIcon（Directory.Build.props）自动提供；
    /// 托盘 NotifyIcon 没法自动继承，要从嵌入资源里取 —— 统一从这里拿，别各写一份。
    /// </summary>
    public static class AppIcon
    {
        private static Icon _cached;

        /// <summary>取嵌入的多尺寸 app.ico。任何失败都退回系统盾牌图标（绝不返回 null）。</summary>
        public static Icon Get()
        {
            if (_cached != null) return _cached;
            try
            {
                using (Stream s = typeof(AppIcon).Assembly.GetManifestResourceStream("LabGuard.Core.Assets.app.ico"))
                {
                    if (s != null) { _cached = new Icon(s); return _cached; }
                }
            }
            catch { }
            _cached = SystemIcons.Shield;
            return _cached;
        }
    }
}
