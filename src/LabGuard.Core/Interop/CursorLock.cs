using System.Windows.Forms;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 全屏遮罩 / 锁定屏期间隐藏鼠标指针。
    ///
    /// 为什么不直接用 <see cref="Cursor"/>：<c>Cursor.Hide()</c> / <c>Cursor.Show()</c>
    /// 底层就是 <c>ShowCursor(false/true)</c>，**带全局计数**：
    /// 多调一次 Hide 就少一次 Show（指针永久消失），多调一次 Show 就再也压不住
    /// （该隐藏时指针却看得见）。遮罩与锁定屏是"上锁→解锁"反复循环的，
    /// 只要有一条路径上 Hide/Show 不等量，这个计数就永久跑偏。
    ///
    /// 这里把调用**归一化**：无论上层喊多少次 Hide/Show，底层最多只压一次、最多只放一次。
    /// 隐藏/显示状态是进程级的（<c>ShowCursor</c> 本身就是），所以用静态状态即可。
    /// </summary>
    public static class CursorLock
    {
        private static readonly object Gate = new object();
        private static bool _hidden;

        /// <summary>隐藏指针（重复调用无副作用）。</summary>
        public static void Hide()
        {
            lock (Gate)
            {
                if (_hidden) return;
                _hidden = true;
                try { Cursor.Hide(); } catch { }
            }
        }

        /// <summary>恢复指针（重复调用无副作用）。</summary>
        public static void Show()
        {
            lock (Gate)
            {
                if (!_hidden) return;
                _hidden = false;
                try { Cursor.Show(); } catch { }
            }
        }

        /// <summary>诊断/自检用：当前是否处于"指针已隐藏"状态。</summary>
        public static bool IsHidden { get { lock (Gate) return _hidden; } }
    }
}
