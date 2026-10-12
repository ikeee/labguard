using LabGuard.Core.Guards;
using LabGuard.Core.Server;

namespace LabGuard.Core.Config
{
    /// <summary>
    /// 暂停状态统一口径（v0.10 服务器版）。本机有**两个互相独立**的暂停来源，
    /// 共用一个标记会出人命（真的会）：老师一恢复就把服务器暂停也清掉，或反之。
    ///
    /// ① 老师本地暂停：`paused.flag`（<see cref="WatchdogGuard.IsPaused"/>）——老师自己恢复；
    /// ② 服务器全员暂停：服务器信封 `paused=1`（<see cref="ServerSyncState.ServerPaused"/>）——只有服务器能解除。
    ///
    /// 语义（协议 §4）：**暂停 = 停止管控策略，进程守护照旧**。
    /// 所以"要不要起引擎 / 要不要继续管控"看 <see cref="IsPaused"/>；
    /// 而"要不要守护小助手进程"只认老师暂停（<see cref="WatchdogGuard.ShouldKeepAgentAlive"/> 的入参口径保持不变）。
    /// </summary>
    public static class PauseState
    {
        public static bool TeacherPaused { get { return WatchdogGuard.IsPaused(); } }
        public static bool ServerPaused { get { return ServerSyncState.ServerPaused; } }
        public static bool IsPaused { get { return TeacherPaused || ServerPaused; } }

        /// <summary>给人看的暂停原因（托盘/日志用）。</summary>
        public static string Describe()
        {
            if (TeacherPaused && ServerPaused) return "已暂停（老师本地暂停 + 服务器全员暂停）";
            if (TeacherPaused) return "已暂停（老师本地暂停）";
            if (ServerPaused) return "已暂停（服务器全员暂停）";
            return "管控中";
        }
    }
}
