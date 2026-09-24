using System;
using System.Runtime.InteropServices;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// Win 键拦截（原版对付"虚拟桌面脱离控制"的最后一环）。
    /// 用低级键盘钩子吞掉 Win 键及其组合（Win+Tab / Win+D 等），只影响本用户会话。
    /// 需要宿主线程有消息循环（Agent 是 WinForms，天然满足）。
    /// </summary>
    public sealed class KeyboardHookGuard : GuardBase
    {
        public override string Name => "Win键拦截";
        public override bool Enabled => Context?.Config.Shell.Enabled == true && Context.Config.Shell.BlockWindowsKey;
        protected override int IntervalMs => 60000;

        private const int WhKeyboardLl = 13;
        private const int WmKeydown = 0x0100;
        private const int WmKeyup = 0x0101;
        private const int WmSyskeydown = 0x0104;
        private const int WmSyskeyup = 0x0105;
        private const uint VkLwin = 0x5B;
        private const uint VkRwin = 0x5C;

        private IntPtr _hook = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc; // 必须保持引用，否则会被 GC 回收
        private int _blocked;

        protected override void OnStart()
        {
            if (Log.DryRun) { SetStatus("干跑模式：未安装键盘钩子"); return; }
            try
            {
                _proc = HookCallback;
                IntPtr module = NativeMethods.GetModuleHandle(null);
                _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
                Log.Info("Win 键拦截钩子：" + (_hook == IntPtr.Zero ? "安装失败" : "已安装"));
            }
            catch (Exception ex)
            {
                Log.Warn("安装键盘钩子失败：" + ex.Message);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WmKeydown || msg == WmKeyup || msg == WmSyskeydown || msg == WmSyskeyup)
                    {
                        var info = (NativeMethods.KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KbdLlHookStruct));
                        if (info.vkCode == VkLwin || info.vkCode == VkRwin)
                        {
                            _blocked++;
                            return (IntPtr)1; // 吞掉
                        }
                    }
                }
            }
            catch { }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        protected override void OnTick()
        {
            SetStatus(_hook == IntPtr.Zero ? "钩子未生效" : "已拦截 Win 键 " + _blocked + " 次");
        }

        protected override void OnStop()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
            }
            catch { }
        }
    }
}
