using System;
using System.Linq;

namespace LabGuard.Core.UI
{
    /// <summary>
    /// 密码门禁的「说法」集中在这里，**不依赖 WinForms**（自检能直接断言）。
    /// 为什么抽出来：这些文案原先散落在 5 个调用点各写一遍，结果出了两类问题——
    ///   ① 设置程序的密码框标题和真正的设置窗口撞脸，老师以为"界面坏了"（实际是被密码拦住了）；
    ///   ② 老师**点了「取消」**，程序也认定是"密码不正确"（<c>ShowDialog() != OK</c> 把两种退出原因混为一谈）。
    /// 抽成纯函数后，这两件事都可以用断言钉住，不再靠人工回头看。
    /// </summary>
    public static class PasswordGate
    {
        /// <summary>
        /// 忘记密码时给老师看的提示。
        /// **这里绝不写任何救援命令或路径**：密码框学生也看得到，把 <c>cleanup-all.ps1</c>、
        /// <c>--force</c> 之类写在上面等于把后门贴给学生（与「遮罩不泄露解锁方式」是同一条规矩，
        /// 救援办法只写在老师自己的介质上：docs/05-防破解与应急.md §6）。
        /// </summary>
        public const string ForgotPasswordTip = "忘记密码请联系机房管理员（本机无法找回）";

        /// <summary>设置主窗口的标题（老师最终要看到的那个窗口）。</summary>
        public static string SettingsWindowTitle()
        {
            return AppInfo.ProductName + " v" + AppInfo.Version + " · 设置（所有功能均可自行开关）";
        }

        /// <summary>密码框的标题：必须让老师一眼看出"这是在要密码，不是设置界面"。</summary>
        public static string WindowTitle(string purpose)
        {
            return "LabGuard · 输入密码（" + purpose + "）";
        }

        /// <summary>老师没输错、只是关掉了密码框（一次都没尝试）→ 不算"密码不正确"。</summary>
        public static bool WasCancelled(int failedAttempts)
        {
            return failedAttempts <= 0;
        }

        /// <summary>
        /// 密码校验没通过时给老师看的结论；<c>null</c> = 老师主动取消，**什么都不用说**（只写日志）。
        /// 以前取消和输错弹同一句"密码不正确"，会把老师引向"我密码记错了"的死循环。
        /// </summary>
        public static string FailureMessage(string action, int failedAttempts)
        {
            if (WasCancelled(failedAttempts)) return null;
            return "密码不正确（本次已试 " + failedAttempts + " 次），不能" + action + "。"
                   + Environment.NewLine + Environment.NewLine + ForgotPasswordTip;
        }

        /// <summary>
        /// 判定一段即将显示在**学生也看得到的界面**上的文字是否泄露了救援手段。
        /// 用法与遮罩的 <c>TipLeaksUnlockMethod</c> 同款：自检断言「必须为 false」。
        /// </summary>
        public static bool LeaksRescueMethod(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] keys =
            {
                "cleanup", "--force", "--init", "--old", "uninstall", ".ps1", "powershell",
                "program files", "programdata", "cmd", "regedit", "schtasks", "sc "
            };
            string lower = text.ToLowerInvariant();
            return keys.Any(lower.Contains);
        }
    }
}
