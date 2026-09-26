using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core.Logging;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 把界面渲染成 PNG（用于文档/截图核对）。
    /// 做法：把窗体挪到屏幕外显示一下（保证句柄与主题都渲染出来），再 DrawToBitmap 存盘。
    /// </summary>
    public static class UiCapture
    {
        private const int OffScreenX = -4000;
        private const int OffScreenY = -4000;

        public static void ShowOffScreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OffScreenX, OffScreenY);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
        }

        public static string Save(Control control, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                control.PerformLayout();
                Application.DoEvents();
                var size = control.Size;
                if (size.Width <= 0 || size.Height <= 0) return null;
                Log.Info("[截图] 开始绘制 " + Path.GetFileName(path) + " (" + size.Width + "x" + size.Height + ")");
                using (var bmp = new Bitmap(size.Width, size.Height))
                {
                    control.DrawToBitmap(bmp, new Rectangle(0, 0, size.Width, size.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                Log.Info("[截图] 已保存 " + path);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("界面截图失败 " + path + "： " + ex.Message);
                return null;
            }
        }

        /// <summary>把窗体按内容高度调整大小（不够高就截到屏幕允许的高度）。</summary>
        public static void FitHeight(Form form, int contentHeight, int minHeight = 320)
        {
            int h = Math.Max(minHeight, Math.Min(contentHeight + 120, 1400));
            form.ClientSize = new Size(form.ClientSize.Width, h);
            form.PerformLayout();
            Application.DoEvents();
        }
    }
}
