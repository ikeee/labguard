using System;
using System.Collections.Generic;

namespace LabGuard.Core.Interop
{
    public enum GestureResult
    {
        None,
        Armed,
        Progress,
        Mistake,
        Timeout,
        Unlocked
    }

    public enum GestureModifierSide
    {
        Unknown = 0,
        Left = 1,
        Right = 2
    }

    /// <summary>
    /// 私有构建注入的解锁资料。公开源码里只有通用状态机，不包含具体组合。
    /// </summary>
    public sealed class UnlockGestureProfile
    {
        public int[] ModifierVks { get; private set; }
        public string[] Sequence { get; private set; }
        public int WindowSeconds { get; private set; }

        public UnlockGestureProfile(int[] modifierVks, string[] sequence, int windowSeconds)
        {
            ModifierVks = modifierVks ?? new int[0];
            Sequence = sequence ?? new string[0];
            WindowSeconds = windowSeconds < 1 ? 2 : windowSeconds;
        }

        public bool IsValid
        {
            get
            {
                if (ModifierVks.Length == 0 || Sequence.Length == 0 || WindowSeconds < 1) return false;
                foreach (int vk in ModifierVks)
                    if (UnlockGesture.ModifierTypeOf(vk, 0, false) == 0) return false;
                foreach (string s in Sequence)
                    if (s != "U" && s != "D" && s != "L" && s != "R") return false;
                return true;
            }
        }
    }

    /// <summary>
    /// 不依赖 WinForms 的教师解锁状态机。
    ///
    /// 顺序是“同一侧按住一组修饰键 → 全部松开 → 在窗口内输入方向序列”。
    /// 小键盘方向键不会被当成方向键；长按产生的 autorepeat 也会被去重。
    /// </summary>
    public sealed class UnlockGesture
    {
        public const int VkShift = 0x10;
        public const int VkControl = 0x11;
        public const int VkAlt = 0x12;
        public const int VkLShift = 0xA0;
        public const int VkRShift = 0xA1;
        public const int VkLControl = 0xA2;
        public const int VkRControl = 0xA3;
        public const int VkLAlt = 0xA4;
        public const int VkRAlt = 0xA5;
        public const int VkLeft = 0x25;
        public const int VkUp = 0x26;
        public const int VkRight = 0x27;
        public const int VkDown = 0x28;

        public const int MaxMistakesPerSession = 20;

        private readonly HashSet<int> _requiredTypes = new HashSet<int>();
        private readonly Dictionary<int, GestureModifierSide> _heldModifiers =
            new Dictionary<int, GestureModifierSide>();
        private readonly HashSet<int> _heldRawKeys = new HashSet<int>();
        private readonly HashSet<int> _heldDirections = new HashSet<int>();

        private GestureModifierSide _comboSide = GestureModifierSide.Unknown;
        private bool _modifierComboComplete;
        private bool _armed;
        private int _position;
        private DateTime _armAt;

        public int WindowSeconds { get; private set; }
        public bool Armed { get { return _armed; } }
        public bool ModifiersHeld { get { return _modifierComboComplete; } }
        public int Position { get { return _position; } }
        public int SequenceLength { get { return _sequence.Length; } }

        private readonly string[] _sequence;

        public UnlockGesture(UnlockGestureProfile profile)
        {
            if (profile == null || !profile.IsValid)
                throw new ArgumentException("unlock profile is invalid", "profile");
            _sequence = (string[])profile.Sequence.Clone();
            WindowSeconds = profile.WindowSeconds;
            foreach (int vk in profile.ModifierVks)
            {
                int type = ModifierTypeOf(vk, 0, false);
                if (type != 0) _requiredTypes.Add(type);
            }
            if (_requiredTypes.Count == 0)
                throw new ArgumentException("unlock profile has no modifiers", "profile");
        }

        /// <summary>测试用构造：资料不含任何真实部署组合。</summary>
        public UnlockGesture(int[] modifierVks, string[] sequence, int windowSeconds)
            : this(new UnlockGestureProfile(modifierVks, sequence, windowSeconds))
        {
        }

        public static string[] ParseSequence(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new string[0];
            var result = new List<string>();
            foreach (string part in raw.Split(new[] { ',', ' ', ';', '|' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string value = part.Trim().ToUpperInvariant();
                if (value == "U" || value == "D" || value == "L" || value == "R")
                    result.Add(value);
                else
                    return new string[0];
            }
            return result.ToArray();
        }

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

        public static bool IsNumpadDirection(int vk)
        {
            return vk >= 0x60 && vk <= 0x69;
        }

        public static bool BruteForceTripped(int mistakes)
        {
            return mistakes >= MaxMistakesPerSession;
        }

        /// <summary>把物理键归一化为修饰键类型：1=Ctrl，2=Alt，3=Shift。</summary>
        public static int ModifierTypeOf(int vk, int scanCode, bool extended)
        {
            switch (vk)
            {
                case VkControl:
                case VkLControl:
                case VkRControl:
                    return 1;
                case VkAlt:
                case VkLAlt:
                case VkRAlt:
                    return 2;
                case VkShift:
                case VkLShift:
                case VkRShift:
                    return 3;
                default:
                    return 0;
            }
        }

        public static GestureModifierSide ModifierSideOf(int vk, int scanCode, bool extended)
        {
            switch (vk)
            {
                case VkLControl:
                case VkLAlt:
                case VkLShift:
                    return GestureModifierSide.Left;
                case VkRControl:
                case VkRAlt:
                case VkRShift:
                    return GestureModifierSide.Right;
            }

            if (vk == VkShift)
                return scanCode == 0x36 ? GestureModifierSide.Right : GestureModifierSide.Left;
            if (vk == VkControl || vk == VkAlt)
                return extended ? GestureModifierSide.Right : GestureModifierSide.Left;
            return GestureModifierSide.Unknown;
        }

        public GestureResult KeyDown(int vk, int scanCode, bool extended, bool injected, DateTime now)
        {
            // 注入按键不参与老师解锁，避免被合成输入伪造。
            if (injected) return GestureResult.None;
            if (_heldRawKeys.Contains(vk)) return GestureResult.None;

            int type = ModifierTypeOf(vk, scanCode, extended);
            if (type != 0)
            {
                _heldRawKeys.Add(vk);
                if (_armed || _position > 0) Reset();

                if (!_requiredTypes.Contains(type)) return GestureResult.None;
                GestureModifierSide side = ModifierSideOf(vk, scanCode, extended);
                if (_heldModifiers.Count == 0)
                    _comboSide = side;
                else if (side != _comboSide)
                {
                    Reset();
                    return GestureResult.Mistake;
                }
                _heldModifiers[type] = side;
                if (_heldModifiers.Count >= _requiredTypes.Count)
                {
                    _modifierComboComplete = true;
                    return GestureResult.Armed;
                }
                return GestureResult.None;
            }

            _heldRawKeys.Add(vk);
            if (_modifierComboComplete)
            {
                Reset();
                return GestureResult.Mistake;
            }
            if (!_armed) return GestureResult.None;

            if ((now - _armAt).TotalSeconds > WindowSeconds)
            {
                Reset();
                return GestureResult.Timeout;
            }

            string symbol = SymbolOf(vk);
            if (symbol == null)
            {
                Reset();
                return GestureResult.Mistake;
            }
            if (_heldDirections.Contains(vk)) return GestureResult.None;
            _heldDirections.Add(vk);

            if (symbol != _sequence[_position])
            {
                Reset();
                return GestureResult.Mistake;
            }

            _position++;
            if (_position >= _sequence.Length)
            {
                Reset();
                return GestureResult.Unlocked;
            }
            return GestureResult.Progress;
        }

        /// <summary>按键抬起；全部松开后才进入方向序列窗口。</summary>
        public void KeyUp(int vk, int scanCode, bool extended, DateTime now)
        {
            _heldRawKeys.Remove(vk);
            _heldDirections.Remove(vk);

            int type = ModifierTypeOf(vk, scanCode, extended);
            if (type == 0 || !_heldModifiers.ContainsKey(type)) return;

            _heldModifiers.Remove(type);
            if (_modifierComboComplete && _heldModifiers.Count == 0 && !_armed)
            {
                _modifierComboComplete = false;
                _armed = true;
                _position = 0;
                _armAt = now;
                _comboSide = GestureModifierSide.Unknown;
            }
        }

        /// <summary>兼容旧调用的抬起入口；只用于测试/辅助，不用于真实钩子。</summary>
        public void KeyUp(int vk)
        {
            KeyUp(vk, 0, false, DateTime.Now);
        }

        /// <summary>兼容旧调用的按下入口；真实钩子应调用带 scan/extended/flags 的重载。</summary>
        public GestureResult KeyDown(int vk, DateTime now)
        {
            return KeyDown(vk, 0, false, false, now);
        }

        public void Reset()
        {
            _armed = false;
            _position = 0;
            _modifierComboComplete = false;
            _comboSide = GestureModifierSide.Unknown;
            // 不清空按住集合：防止长按 autorepeat 在 Reset 后重新计数；
            // 对应按键抬起时再逐个移除。
        }
    }
}
