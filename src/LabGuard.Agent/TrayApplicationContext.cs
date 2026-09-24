using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LabGuard.Core;
using LabGuard.Core.Config;
using LabGuard.Core.Guards;
using LabGuard.Core.Logging;
using LabGuard.Core.UI;

namespace LabGuard.Agent
{
    /// <summary>
    /// 托盘小助手（about.exe）：
    /// 系统功能（F）→ 设置 / 启动程序 / 暂停监控 / 退出程序；退出与暂停都要密码。
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext, ILockScreen, IAlertScreen
    {
        private readonly NotifyIcon _tray;
        private readonly GuardEngine _engine;
        private readonly LockScreenForm _lockScreen;
        private readonly DisconnectMaskForm _disconnectMask = new DisconnectMaskForm();
        private readonly string _installDir;
        private readonly bool _dryRun;
        private GuardConfig _config;
        private bool _paused;

        public TrayApplicationContext(GuardConfig config, bool dryRun)
        {
            _config = config;
            _dryRun = dryRun;
            _installDir = GuardEngine.RequireInstallDir();
            _lockScreen = new LockScreenForm(_config.PasswordHash);
            _engine = new GuardEngine(_config);

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Visible = true,
                Text = "LabGuard"
            };
            _tray.DoubleClick += (s, e) => ShowStatus();
            _tray.ContextMenuStrip = BuildMenu();

            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                _tray.BalloonTipTitle = "LabGuard";
                _tray.BalloonTipText = "尚未设置密码，监控未启用。请右键托盘图标 → 系统功能 → 设置。";
                _tray.ShowBalloonTip(6000);
                Log.Warn("未设置密码，处于「未配置」状态，不启用任何管控");
            }
            else if (Core.Interop.SafeModeDetector.ShouldSkipEnforcement(_config.SafeMode.RunInSafeMode))
            {
                _paused = true;
                Balloon("LabGuard", "当前是安全模式：按设置不加载管控。你可以在这种模式下修复或彻底卸载。");
            }
            else if (_config.Enabled && !WatchdogGuard.IsPaused())
            {
                StartEngine();
            }
            else
            {
                _paused = true;
                UpdateTrayText();
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { Font = new Font("微软雅黑", 9F) };

            var systemMenu = new ToolStripMenuItem("系统功能（F）");
            systemMenu.DropDownItems.Add("设置", null, (s, e) => OpenSettings());
            systemMenu.DropDownItems.Add("启动/重启监控", null, (s, e) => StartEngine());
            systemMenu.DropDownItems.Add(new ToolStripSeparator());
            systemMenu.DropDownItems.Add("暂停监控（需密码）", null, (s, e) => PauseEngine());
            systemMenu.DropDownItems.Add("退出程序（需密码）", null, (s, e) => ExitProgram());
            menu.Items.Add(systemMenu);

            menu.Items.Add("查看当前状态", null, (s, e) => ShowStatus());
            menu.Items.Add("打开日志目录", null, (s, e) => OpenPath(Log.Directory));
            menu.Items.Add("打开安装目录", null, (s, e) => OpenPath(_installDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于本程序", null, (s, e) => ShowAbout());
            return menu;
        }

        private void StartEngine()
        {
            try
            {
                _config = ConfigStore.Load();
                // 后路：安全模式下即使手动点"启动监控"也不加载（老师可在安全模式里修/卸载完再回正常模式）
                if (Core.Interop.SafeModeDetector.ShouldSkipEnforcement(_config.SafeMode.RunInSafeMode))
                {
                    _paused = true;
                    UpdateTrayText();
                    Balloon("LabGuard", "当前是安全模式：不启用监控（如需在安全模式也管控，请在设置里打开\"安全模式也运行\"）。");
                    return;
                }
                WatchdogGuard.MarkPaused(false);
                _engine.Start(this, _installDir, _dryRun, EngineRole.Full);
                _paused = false;
                UpdateTrayText();
                Balloon("LabGuard", "监控已启动" + (_dryRun ? "（干跑模式：只记录，不修改系统）" : ""));
            }
            catch (Exception ex)
            {
                Log.Error("启动监控失败", ex);
                Balloon("LabGuard", "启动监控失败，详见日志");
            }
        }

        private void PauseEngine()
        {
            if (!Verify("暂停监控")) return;
            _engine.Stop(markPaused: true);
            _paused = true;
            UpdateTrayText();
            Balloon("LabGuard", "已暂停监控：现在可以使用U盘、下载文件、运行被拦截的软件。" +
                                    (_config.ResumeAfterMinutes > 0
                                        ? "（" + _config.ResumeAfterMinutes + " 分钟后自动恢复）"
                                        : "（需手动恢复）"));
        }

        private void ExitProgram()
        {
            if (!Verify("退出程序")) return;
            _engine.Stop(markPaused: true);
            // 与服务一起退出，保证系统状态被还原
            Core.Interop.SystemActions.Run("net.exe", "stop " + WatchdogGuard.ServiceName);
            _tray.Visible = false;
            Balloon("LabGuard", "已退出并还原被改动的系统设置。");
            ExitThread();
        }

        private bool Verify(string action)
        {
            if (string.IsNullOrEmpty(_config.PasswordHash))
            {
                Balloon("LabGuard", "尚未设置密码，请先运行设置程序。");
                return false;
            }
            using (var dlg = new PasswordDialog("LabGuard - " + action, action + "密码：", _config.PasswordHash))
            {
                return dlg.ShowDialog() == DialogResult.OK;
            }
        }

        private void OpenSettings()
        {
            string exe = Path.Combine(_installDir, "LabGuard.Settings.exe");
            if (!File.Exists(exe)) { Balloon("LabGuard", "未找到设置程序：" + exe); return; }
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("启动设置程序失败：" + ex.Message); }
        }

        private void ShowStatus()
        {
            string state = _paused ? "已暂停" : (_engine.IsRunning ? "监控中" : "未启动");
            string body = "小助手工作状态：" + state + Environment.NewLine +
                          (_dryRun ? "运行模式：干跑（不修改系统）" : "运行模式：正常") + Environment.NewLine + Environment.NewLine +
                          _engine.StatusSnapshot;
            MessageBox.Show(body, "学生LabGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                LabGuard.Core.AppInfo.ProductName + "  v" + LabGuard.Core.AppInfo.Version + Environment.NewLine +
                "基于对《LabGuard v13.03》的原理分析实现，功能等价、源码开放。" + Environment.NewLine +
                "项目地址：" + LabGuard.Core.AppInfo.RepoUrl + Environment.NewLine +
                "配置目录：" + ConfigStore.DataDir + Environment.NewLine +
                "日志目录：" + Log.Directory,
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        private void UpdateTrayText()
        {
            string state = _paused ? "已暂停" : (_engine.IsRunning ? "监控中" : "未启动");
            _tray.Text = ("LabGuard - " + state).Substring(0, Math.Min(60, 8 + state.Length));
        }

        private void Balloon(string title, string text)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(4000);
            }
            catch { }
        }

        // ------------------------------------------------------------------ ILockScreen
        void ILockScreen.Show(string title, string message, bool fakeBlueScreen)
        {
            _lockScreen.ShowLock(title, message, fakeBlueScreen, null);
        }

        void ILockScreen.Notify(string title, string message) => Balloon(title, message);

        void IAlertScreen.ShowDisconnectMask(string machineNumber, string headLine, string advice, string passwordHash,
            bool randomWallpaper, bool showElapsed, Func<string> elapsedText, Func<bool> networkRestored,
            Action teacherUnlock)
        {
            _disconnectMask.ShowMask(machineNumber, headLine, advice,
                string.IsNullOrEmpty(passwordHash) ? _config.PasswordHash : passwordHash,
                randomWallpaper, showElapsed, elapsedText, networkRestored,
                () =>
                {
                    // 老师用密码解除 → 暂停监控（否则 10 秒后又被弹出来）
                    try { teacherUnlock?.Invoke(); } catch { }
                    PauseBecauseTeacherUnlock();
                });
        }

        void IAlertScreen.Beep(int times, int intervalMs)
        {
            // 响鸣放到后台线程，避免阻塞轮询；次数有限（默认 3 次 × 700ms）
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < times; i++)
                    {
                        Console.Beep(880, Math.Max(80, intervalMs / 2));
                        System.Threading.Thread.Sleep(intervalMs / 2);
                    }
                }
                catch { }
            });
        }

        private void PauseBecauseTeacherUnlock()
        {
            _engine.Stop(markPaused: true);
            _paused = true;
            UpdateTrayText();
            Balloon("LabGuard", "已按老师密码解除断网遮罩，监控已暂停（设置里保存或重启小助手可恢复）。");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _engine.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
