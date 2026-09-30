using System;
using System.Collections.Generic;

namespace LabGuard.Core.Interop
{
    /// <summary>
    /// 一次 <c>KeyDown</c> 的结果（供调用方决定要不要给老师反馈）。
    /// </summary>
    public enum GestureResult
    {
        /// <summary>这个键与手势无关（未激活时的杂键 / 非方向键 / autorepeat）。</summary>
        None,
        /// <summary>刚按下激活键，进入待输入状态。</summary>
        Armed,
        /// <summary>序列推进了一格。</summary>
        Progress,
        /// <summary>走错一位，整段重置（通常是老师手滑）。</summary>
        Mistake,
        /// <summary>超过时间窗口，整段重置。</summary>
        Timeout,
        /// <summary>序列完成，应当解锁。</summary>
        Unlocked
    }

    /// <summary>
    /// 老师的「按键手势」解锁：<c>Pause/Break</c> 激活 → <c>↑↑↓↓←→←→</c> 解锁。
    ///
    /// 做成**不依赖 WinForms 的纯逻辑类**，是为了能直接在自检里断言（否则只能弹窗人工看，
    /// 而这条通道历史上正是因为没有可断言入口才连续三轮漏验）。
    ///
    /// 三个必须钉死的细节（都是本机低级键盘钩子实测得出的，见 docs/06）：
    /// · **autorepeat 必须去重**：长按会连发 keydown 而中间一个 keyup 都没有，
    ///   不去重的话老师手一抖多按半秒，序列就被冲乱、再也解锁不了。
    /// · **激活键只能是单键**：Ctrl+Pause 在钩子里依然是 vk 0x13（不会变成 VK_CANCEL），
    ///   所以"按住 Ctrl 再按 Pause"这种复合激活根本区分不出来。
    /// · **小键盘不算方向键**：8/2/4/6 报的是 VK_NUMPAD*，且受 NumLock 影响，一律不接受。
    /// </summary>
    public sealed class UnlockGesture
    {
        public const int VkPause = 0x13;
        public const int VkScrollLock = 0x91;
        public const int VkLeft = 0x25;
        public const int VkUp = 0x26;
        public const int VkRight = 0x27;
        public const int VkDown = 0x28;

        /// <summary>默认序列（Konami Code 的方向键部分）。</summary>
        public static readonly string[] DefaultSequence = { "U", "U", "D", "D", "L", "R", "L", "R" };

        /// <summary>激活键的虚拟键码。</summary>
        public int ArmVk { get; private set; }

        /// <summary>待匹配的序列（已归一化为 U/D/L/R）。</summary>
        public string[] Sequence { get; private set; }

        /// <summary>激活后必须在此秒数内输完，超时自动重置。</summary>
        public int WindowSeconds { get; private set; }

        /// <summary>是否已激活（已按下激活键、正在等待序列）。</summary>
        public bool Armed { get; private set; }

        /// <summary>已匹配的位数。</summary>
        public int Position { get; private set; }

        private int _heldVk;            // 当前按住尚未抬起的键（autorepeat 去重用）
        private DateTime _armAt;

        public UnlockGesture(string armKey, string sequence, int windowSeconds)
        {
            ArmVk = VkForArmKey(armKey);
            Sequence = ParseSequence(sequence);
            WindowSeconds = windowSeconds < 1 ? 1 : windowSeconds;
        }

        /// <summary>激活键名 → 虚拟键码。未知名字一律退回 Pause。</summary>
        public static int VkForArmKey(string name)
        {
            if (string.Equals(name, "ScrollLock", StringComparison.OrdinalIgnoreCase)) return VkScrollLock;
            return VkPause;
        }

        /// <summary>
        /// 解析序列文本（"U,U,D,D,L,R,L,R"）。只接受 U/D/L/R；
        /// 空或含非法内容时**退回默认序列**——宁可让老师用不成自定义，也不能让他彻底解锁不了。
        /// </summary>
        public static string[] ParseSequence(string raw)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (string part in raw.Split(new[] { ',', ' ', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string s = part.Trim().ToUpperInvariant();
                    if (s.Length != 1) return (string[])DefaultSequence.Clone();
                    if (s != "U" && s != "D" && s != "L" && s != "R") return (string[])DefaultSequence.Clone();
                    list.Add(s);
                }
            }
            if (list.Count == 0) return (string[])DefaultSequence.Clone();
            return list.ToArray();
        }

        /// <summary>方向键 → 符号；不是方向键返回 null（小键盘 VK_NUMPAD* 也返回 null）。</summary>
        public static string SymbolOf(int vk)
        {
            switch (vk)
            {
                case VkUp: return "U";
                case VkDown: return "D";
                case VkLeft: return "L";
                case VkRight: return "R";
                default: return null;
            }
        }

        /// <summary>
        /// 抬起：清掉"按住"标记，允许同一个键再次计数。
        /// </summary>
        public void KeyUp(int vk)
        {
            if (vk == _heldVk) _heldVk = 0;
        }

        /// <summary>
        /// 按下。返回本次按键产生的状态变化；返回 <see cref="GestureResult.Unlocked"/> 时表示应当解锁。
        /// </summary>
        public GestureResult KeyDown(int vk, DateTime now)
        {
            // autorepeat：长按不放会连发 keydown，中间没有 keyup —— 同一个键必须先抬起才再算一次
            if (vk == _heldVk) return GestureResult.None;
            _heldVk = vk;

            if (!Armed)
            {
                if (vk != ArmVk) return GestureResult.None;
                Armed = true;
                Position = 0;
                _armAt = now;
                return GestureResult.Armed;
            }

            if ((now - _armAt).TotalSeconds > WindowSeconds)
            {
                Reset();
                return GestureResult.Timeout;
            }

            string sym = SymbolOf(vk);
            if (sym == null) return GestureResult.None;      // 杂键：忽略，不打断老师

            if (sym != Sequence[Position])
            {
                Reset();
                return GestureResult.Mistake;
            }

            Position++;
            if (Position >= Sequence.Length)
            {
                Reset();
                return GestureResult.Unlocked;
            }
            return GestureResult.Progress;
        }

        /// <summary>重置到"未激活"。注意不清 _heldVk——键还按着时不能立刻重复计数。</summary>
        public void Reset()
        {
            Armed = false;
            Position = 0;
        }
    }
}
