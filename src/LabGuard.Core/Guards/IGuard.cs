using System;
using System.Collections.Generic;
using LabGuard.Core.Config;

namespace LabGuard.Core.Guards
{
    public enum ViolationAction { Notify, Kill, Lock, Shutdown, Reboot }

    public class ViolationEventArgs : EventArgs
    {
        public string Guard { get; set; }
        public string Message { get; set; }
        public ViolationAction Action { get; set; }
        public string Detail { get; set; }
        public bool Repeatable { get; set; } = true;
    }

    /// <summary>锁定/提示界面由宿主（Agent 的 WinForms）实现，核心只发号施令。</summary>
    public interface ILockScreen
    {
        /// <summary>全屏锁定并显示提示（返回后用户已解锁）。</summary>
        void Show(string title, string message, bool fakeBlueScreen);
        /// <summary>右下角气泡/轻提示。</summary>
        void Notify(string title, string message);
    }

    /// <summary>只有"用户会话界面"才能实现的提醒能力（服务侧为 null，会自动降级为只记日志）。</summary>
    public interface IAlertScreen
    {
        /// <summary>
        /// 断网遮罩（"屏保式"）：全屏显示、吞掉键鼠、**插回网线即自动消失**（不需要老师）。
        /// teacherUnlock 为老师用密码强制解除时的回调（用于暂停监控，避免又被弹出来）。
        /// </summary>
        void ShowDisconnectMask(string machineNumber, string headLine, string advice, string passwordHash,
            bool randomWallpaper, bool showElapsed, Func<string> elapsedText, Func<bool> networkRestored,
            Action teacherUnlock);

        /// <summary>响鸣提醒（次数有限）。</summary>
        void Beep(int times, int intervalMs);
    }

    public class GuardContext
    {
        public GuardConfig Config { get; set; }
        public ILockScreen Screen { get; set; }
        public string InstallDir { get; set; }
        public event EventHandler<ViolationEventArgs> Violation;

        public GuardContext(GuardConfig config, string installDir, ILockScreen screen)
        {
            Config = config;
            InstallDir = installDir;
            Screen = screen;
        }

        /// <summary>提醒界面（服务侧为 null）。</summary>
        public IAlertScreen Alert => Screen as IAlertScreen;

        /// <summary>Guard 统一的上报入口：记录 + 通知宿主 + 按策略动作处置。</summary>
        public void Report(string guard, string message, ViolationAction action, string detail = null)
        {
            var handler = Violation;
            if (handler != null)
            {
                handler(this, new ViolationEventArgs
                {
                    Guard = guard,
                    Message = message,
                    Action = action,
                    Detail = detail
                });
            }
        }

        public List<string> StatusList { get; } = new List<string>();
        public void SetStatus(string guard, string status)
        {
            lock (StatusList)
            {
                StatusList.RemoveAll(s => s.StartsWith(guard + "：", StringComparison.Ordinal));
                StatusList.Add(guard + "：" + status);
            }
        }
    }

    public interface IGuard : IDisposable
    {
        string Name { get; }
        string Status { get; }
        void Start(GuardContext context);
        /// <summary>停止并在需要时把系统状态还原（"退出小助手"）。</summary>
        void Stop();
    }
}
