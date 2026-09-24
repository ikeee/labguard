using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// 新文件 / 下载监控（文件系统监控）：
    /// 监控指定目录，出现高危扩展名的新文件即删除并提示；
    /// 三档模式：全部允许 / 仅 C++ 可生成新 exe / 全部禁止。
    /// </summary>
    public sealed class FileCreationGuard : GuardBase
    {
        public override string Name => "新文件/下载监控";
        public override bool Enabled => Context?.Config.FileCreation.Enabled ?? false;
        protected override int IntervalMs => 60000;

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private DateTime _boot = DateTime.Now;
        private int _strikes;

        protected override void OnStart()
        {
            var cfg = Context.Config.FileCreation;
            if (string.Equals(cfg.Mode, "AllowAll", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("全部允许（不删除）");
                return;
            }

            foreach (string raw in cfg.WatchPaths)
            {
                string path = Environment.ExpandEnvironmentVariables(raw ?? "");
                if (string.IsNullOrWhiteSpace(path)) continue;
                // 用户目录里的 Downloads 是重点，逐用户展开
                if (path.IndexOf("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (string userDir in SafeGetDirectories(@"C:\Users"))
                        Attach(Path.Combine(userDir, "Downloads"));
                    continue;
                }
                Attach(path);
            }
            SetStatus("监控 " + _watchers.Count + " 个目录（模式：" + cfg.Mode + "）");
        }

        private void Attach(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                bool isRoot = path.TrimEnd('\\').Length == 2;
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = !isRoot,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 32 * 1024,
                    EnableRaisingEvents = false
                };
                w.Created += OnChanged;
                w.Renamed += OnRenamed;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
                Log.Debug("监控目录：" + path);
            }
            catch (Exception ex) { Log.Warn("监控目录失败 " + path + " :: " + ex.Message); }
        }

        private static IEnumerable<string> SafeGetDirectories(string root)
        {
            try { return Directory.GetDirectories(root); }
            catch { return new string[0]; }
        }

        private void OnRenamed(object sender, RenamedEventArgs e) => Handle(e.FullPath);
        private void OnChanged(object sender, FileSystemEventArgs e) => Handle(e.FullPath);

        private void Handle(string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullPath)) return;
                if (fullPath.StartsWith(Config.ConfigStore.DataDir, StringComparison.OrdinalIgnoreCase)) return;
                string ext = Path.GetExtension(fullPath);
                if (string.IsNullOrEmpty(ext)) return;

                var cfg = Context.Config.FileCreation;
                bool highRisk = cfg.HighRiskExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
                bool allowed = cfg.AllowedExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
                if (!highRisk || allowed) return;

                string mode = (cfg.Mode ?? "CppOnly").Trim().ToUpperInvariant();
                if (mode == "ALLOWALL") return;
                if (mode == "CPPONLY" && string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) == false)
                {
                    // 仅允许 C++ 生成新 exe：其它高危扩展名依旧拦截
                }

                string message = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                    ? (mode == "CPPONLY"
                        ? "本机禁止生成新的 exe 文件；需要安装软件时请老师用密码暂停管控。"
                        : "本机禁止生成新的 exe/dll 文件。")
                    : string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase)
                        ? "本机禁止生成新的 msi 等高危文件。"
                        : string.Equals(ext, ".reg", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase)
                            ? "本机禁止生成新的 reg/bat 等高危文件。"
                            : "本机禁止下载/安装软件；如确有需要，请老师用密码暂停管控。";

                _strikes++;
                bool delete = string.Equals(cfg.Action, "Delete", StringComparison.OrdinalIgnoreCase);
                SystemActions.DeleteFile(fullPath);
                Context.Report(Name, message + "  刚刚删除文件：" + fullPath,
                    _strikes >= 2 ? ViolationAction.Lock : ViolationAction.Notify, ext);
                SetStatus("已删除违规新文件（累计 " + _strikes + " 次）");
            }
            catch (Exception ex) { Log.Debug("处理新文件事件失败：" + ex.Message); }
        }

        protected override void OnTick()
        {
            SetStatus(string.Equals(Context.Config.FileCreation.Mode, "AllowAll", StringComparison.OrdinalIgnoreCase)
                ? "全部允许（不删除）"
                : "监控 " + _watchers.Count + " 个目录，已拦截 " + _strikes + " 个文件");
        }

        protected override void OnStop()
        {
            foreach (FileSystemWatcher w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
        }
    }
}
