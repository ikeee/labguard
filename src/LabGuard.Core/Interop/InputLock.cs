using System;
using System.Threading;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 全屏锁定 / 断网遮罩期间把**鼠标键盘**一起交给系统层拦住（BlockInput）。
    ///
    /// 为什么把"持锁"放在本类自己的线程上——官方文档（winuser.h · BlockInput）写明两点：
    ///   ① 只有调用 BlockInput 的那个线程才能解除；
    ///   ② 该线程意外退出时，系统会自动清理并恢复输入（cleans up properly and re-enables input）。
    /// 所以配一条带心跳的专用线程：界面卡死/崩溃 → 心跳停止 → 线程自己退出 → 系统自动恢复输入，
    /// 不会把老师锁在机器外面；Ctrl+Alt+Del 也是 Windows 永远保留的救援通道。
    ///
    /// 需要管理员权限才生效（代理由计划任务 `/rl highest` 启动，满足）；非管理员时静默失败并记日志。
    /// </summary>
    public static class InputLock
    {
        /// <summary>不限制（只靠全屏置顶窗口 + 键盘钩子）。</summary>
        public const string ModeOff = "Off";

        /// <summary>用 BlockInput 把鼠标键盘一起锁住（默认）。</summary>
        public const string ModeOn = "On";

        /// <summary>心跳超时（毫秒）：界面这么久没来心跳就认为它已无响应，主动解锁。</summary>
        private const int BeatTimeoutMs = 10000;

        private static readonly object Gate = new object();
        private static Thread _thread;
        private static volatile bool _stop;
        private static DateTime _beatUtc = DateTime.UtcNow;
        private static int _holds;

        /// <summary>配置值归一化：只认 Off，其它一律按 On 处理。</summary>
        public static string Normalize(string mode)
        {
            return string.Equals(mode, ModeOff, StringComparison.OrdinalIgnoreCase) ? ModeOff : ModeOn;
        }

        /// <summary>是否正在硬锁（供自检/日志用）。</summary>
        public static bool IsActive { get { lock (Gate) return _thread != null; } }

        /// <summary>
        /// 当前引用计数（诊断用）。&gt; 0 表示还有人"持有"硬锁；
        /// 界面都已解除却仍 &gt; 0，就是"键鼠锁死"类事故的直接线索。
        /// </summary>
        public static int HoldsCount { get { lock (Gate) return _holds; } }

        /// <summary>开始限制输入（可重复调用，引用计数）。</summary>
        public static void Engage(string mode)
        {
            if (Normalize(mode) == ModeOff) return;
            lock (Gate)
            {
                _holds++;
                _beatUtc = DateTime.UtcNow;
                if (_thread != null) return;      // 已经在锁着
                _stop = false;
                _thread = new Thread(BlockerBody) { IsBackground = true, Name = "LabGuard-InputLock" };
                _thread.Start();
            }
        }

        /// <summary>心跳：界面还活着就定期调用（遮罩/锁屏的计时器里调用）。</summary>
        public static void KeepAlive()
        {
            lock (Gate) _beatUtc = DateTime.UtcNow;
        }

        /// <summary>解除限制（引用计数归零才真正解锁）。</summary>
        public static void Disengage()
        {
            lock (Gate)
            {
                if (_holds > 0) _holds--;
                _beatUtc = DateTime.UtcNow;
                if (_holds == 0) _stop = true;    // 线程会在 250ms 内解除并退出
            }
        }

        private static void BlockerBody()
        {
            bool blocked = false;
            try
            {
                blocked = NativeMethods.BlockInput(true);
                if (!blocked)
                {
                    Log.Warn("输入硬锁未生效（通常是没有管理员权限，或已被其它程序占用）：本次只保留全屏窗口与键盘钩子");
                    return;
                }
                Log.Info("输入硬锁已开启：鼠标键盘由系统层拦截（Ctrl+Alt+Del 仍可取回输入）");

                while (true)
                {
                    Thread.Sleep(250);
                    bool stop, stale;
                    lock (Gate)
                    {
                        stop = _stop;
                        stale = (DateTime.UtcNow - _beatUtc).TotalMilliseconds > BeatTimeoutMs;
                    }
                    if (stop) break;
                    if (stale)
                    {
                        Log.Warn("输入硬锁心跳超时（界面可能已无响应）→ 主动解锁，避免把老师锁在机器外");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("输入硬锁线程异常：" + ex.Message);
            }
            finally
            {
                if (blocked)
                {
                    try { NativeMethods.BlockInput(false); }
                    catch (Exception ex) { Log.Error("解除输入硬锁失败（可能仍处于锁定状态）", ex); }
                }
                lock (Gate)
                {
                    _thread = null;
                    _stop = false;
                    _holds = 0;
                }
                if (blocked) Log.Info("输入硬锁已解除");
            }
        }
    }
}
