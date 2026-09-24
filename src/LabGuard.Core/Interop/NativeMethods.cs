using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LabGuard.Core.Interop
{
    internal static class NativeMethods
    {
        // ---- 进程挂起/恢复与系统权限（所需的最小权限） ----
        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int RtlAdjustPrivilege(int privilege, bool enable, bool currentThread, ref bool enabled);

        // ---- 窗口 / 前台窗口（用于按标题识别违规窗口） ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool BlockInput(bool blockIt);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetModuleHandle(string name);

        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct KbdLlHookStruct
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PathIsDirectory(string path);
    }
}
