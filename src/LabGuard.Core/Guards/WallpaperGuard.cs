using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 桌面壁纸与机器编号（6 张规范壁纸 + 右上角显示计算机名后 6 位）。
    /// 按素材实时合成一张带编号的壁纸，避免直接依赖外部素材。
    /// </summary>
    public sealed class WallpaperGuard : GuardBase
    {
        public override string Name => "桌面壁纸与编号";
        public override bool Enabled => Context?.Config.Wallpaper.Enabled ?? false;
        protected override int IntervalMs => 20000;

        private const int SpiSetDeskWallpaper = 20;
        private const uint SpifUpdateIniFile = 0x01;
        private const uint SpifSendChange = 0x02;

        private string _applied;

        protected override void OnStart()
        {
            _applied = BuildWallpaper();
            if (_applied == null) { SetStatus("未设置壁纸（缺少素材）"); return; }
            ApplyWallpaper(_applied);
        }

        protected override void OnTick()
        {
            if (_applied == null) { SetStatus("未设置壁纸（缺少素材）"); return; }
            if (!Context.Config.Wallpaper.GuardWallpaper) { SetStatus("已设置（未守护）"); return; }
            string current = Convert.ToString(SystemActions.GetRegistryValue(
                RegistryHive.CurrentUser, @"Control Panel\Desktop", "Wallpaper"));
            if (!string.Equals(current, _applied, StringComparison.OrdinalIgnoreCase))
            {
                ApplyWallpaper(_applied);
                Context.Report(Name, "桌面壁纸被修改，已恢复。", ViolationAction.Notify);
                SetStatus("壁纸被改，已恢复");
            }
            else
            {
                SetStatus("壁纸生效中（编号 " + MachineNumber() + "）");
            }
        }

        private string MachineNumber()
        {
            int n = Math.Max(1, Context.Config.Wallpaper.MachineNumberChars);
            string name = Environment.MachineName;
            return name.Length <= n ? name : name.Substring(name.Length - n);
        }

        private string BuildWallpaper()
        {
            string source = Context.Config.Wallpaper.Image;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                string dir = Path.Combine(Context.InstallDir, "wallpaper");
                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir, "*.jpg");
                    if (files.Length > 0) source = files[new Random().Next(files.Length)];
                }
            }
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                Log.Warn("没有可用的壁纸素材（可在设置里指定图片）");
                return null;
            }
            if (Log.DryRun)
            {
                Log.Info("[dry] 将生成并设置壁纸（编号 " + MachineNumber() + "），素材 " + source);
                return null;
            }
            try
            {
                string outPath = Path.Combine(Config.ConfigStore.DataDir, "wallpaper-active.jpg");
                Directory.CreateDirectory(Config.ConfigStore.DataDir);
                using (Image src = Image.FromFile(source))
                using (var bmp = new Bitmap(src.Width, src.Height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
                    if (Context.Config.Wallpaper.ShowMachineNumber)
                    {
                        string text = MachineNumber();
                        float size = src.Height * 0.05f;
                        using (var font = new Font("黑体", size, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                        using (var brush = new SolidBrush(Color.White))
                        {
                            var fmt = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                            var rect = new RectangleF(0, src.Height * 0.04f, src.Width - src.Width * 0.02f, size * 1.6f);
                            g.TextRenderingHint = TextRenderingHint.AntiAlias;
                            g.DrawString(text, font, shadow, new RectangleF(rect.X + 3, rect.Y + 3, rect.Width, rect.Height), fmt);
                            g.DrawString(text, font, brush, rect, fmt);
                        }
                    }
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                Log.Info("已生成壁纸（含机器编号 " + MachineNumber() + "）：" + outPath);
                return outPath;
            }
            catch (Exception ex)
            {
                Log.Warn("生成壁纸失败：" + ex.Message);
                return null;
            }
        }

        private void ApplyWallpaper(string path)
        {
            SystemActions.SetRegistryValue(RegistryHive.CurrentUser, @"Control Panel\Desktop", "Wallpaper", path, RegistryValueKind.String);
            SystemActions.SetRegistryValue(RegistryHive.CurrentUser, @"Control Panel\Desktop", "WallpaperStyle", "10", RegistryValueKind.String);
            SystemActions.SetRegistryValue(RegistryHive.CurrentUser, @"Control Panel\Desktop", "TileWallpaper", "0", RegistryValueKind.String);
            if (Log.DryRun) { Log.Info("[dry] 将设置壁纸 " + path); return; }
            try
            {
                NativeMethods.SystemParametersInfo(SpiSetDeskWallpaper, 0, path, SpifUpdateIniFile | SpifSendChange);
            }
            catch (Exception ex) { Log.Warn("设置壁纸失败：" + ex.Message); }
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            string windowsDefault = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
            if (File.Exists(windowsDefault)) ApplyWallpaper(windowsDefault);
        }
    }
}
