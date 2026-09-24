using System;
using System.Runtime.InteropServices;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 是否处于"安全模式"。
    /// 用途：给老师留一条后路——安全模式下默认**不加载任何管控**，
    /// 这样即使程序出问题、服务被破坏、或要彻底卸载，进安全模式就能干净处理。
    /// </summary>
    public static class SafeModeDetector
    {
        private const int SM_CLEANBOOT = 67;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        /// <summary>0 = 正常启动；1 = 安全模式；2 = 带网络连接的安全模式。</summary>
        public static int BootMode
        {
            get
            {
                try { return GetSystemMetrics(SM_CLEANBOOT); }
                catch { return 0; }
            }
        }

        public static bool IsSafeMode => BootMode != 0;
        public static bool IsSafeModeWithNetwork => BootMode == 2;

        /// <summary>本进程是否应该跳过管控（安全模式 + 配置要求安全模式不运行）。</summary>
        public static bool ShouldSkipEnforcement(bool runInSafeMode)
        {
            if (!IsSafeMode) return false;
            if (runInSafeMode) return false;
            Log.Warn("检测到安全模式（模式=" + BootMode + "）：按设置跳过全部管控，留给老师处理。");
            return true;
        }
    }
}
