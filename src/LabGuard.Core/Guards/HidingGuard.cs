using System;
using System.IO;
using LabGuard.Core.Config;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 安装目录隐藏 / 显示（**可在设置里开关**）：
    /// 开启时给安装目录与其中的文件加上 Hidden + System 属性，学生在资源管理器里不容易发现；
    /// 关闭时立即去掉这两个属性，老师和维护人员能正常看到。
    ///
    /// 注意：这只影响"显示"，不改变权限（权限由安装时的 ACL 控制）。
    /// </summary>
    public sealed class HidingGuard : GuardBase
    {
        public override string Name => "安装目录隐藏";
        public override bool Enabled => Context?.Config.Watchdog.Enabled == true;
        protected override int IntervalMs => 30000;

        private string TargetDir => string.IsNullOrEmpty(ConfigStore.InstallDir)
            ? Context?.InstallDir
            : ConfigStore.InstallDir;

        protected override void OnStart()
        {
            Apply(Context.Config.Watchdog.HideInstallDir, Context.Config.Watchdog.HideInstallDir);
        }

        protected override void OnTick()
        {
            bool want = Context.Config.Watchdog.HideInstallDir;
            if (IsHidden(TargetDir) != want) Apply(want, want);
            SetStatus(want ? "安装目录已隐藏" : "安装目录可见");
        }

        private void Apply(bool hidden, bool report)
        {
            string dir = TargetDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                SetStatus("未找到安装目录");
                return;
            }
            if (Log.DryRun)
            {
                Log.Info("[dry] 将把安装目录设为" + (hidden ? "隐藏" : "可见") + "：" + dir);
                SetStatus(hidden ? "安装目录已隐藏（干跑）" : "安装目录可见（干跑）");
                return;
            }
            try
            {
                SetAttrs(dir, hidden);
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    SetAttrs(f, hidden);
                }
                if (report) Log.Info("安装目录已设为" + (hidden ? "隐藏" : "可见") + "：" + dir);
                SetStatus(hidden ? "安装目录已隐藏" : "安装目录可见");
            }
            catch (Exception ex)
            {
                Log.Warn("设置安装目录隐藏属性失败：" + ex.Message);
                SetStatus("设置隐藏属性失败");
            }
        }

        private static void SetAttrs(string path, bool hidden)
        {
            FileAttributes attr = File.GetAttributes(path);
            attr = hidden
                ? (attr | FileAttributes.Hidden | FileAttributes.System)
                : (attr & ~(FileAttributes.Hidden | FileAttributes.System));
            File.SetAttributes(path, attr);
        }

        private static bool IsHidden(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.Hidden) == FileAttributes.Hidden;
            }
            catch { return false; }
        }

        protected override void OnStop()
        {
            // 退出/卸载时恢复可见，避免老师自己找不到
            try { Apply(false, true); } catch { }
        }
    }
}
