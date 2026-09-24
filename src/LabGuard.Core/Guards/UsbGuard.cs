using System;
using LabGuard.Core.Interop;
using LabGuard.Core.Logging;
using Microsoft.Win32;

namespace LabGuard.Core.Guards
{
    /// <summary>
    /// USB 存储设备控制（）：禁用 usbstor 驱动（Start=4），
    /// 保留 USB 键鼠；被改回则提示并重新禁用。
    /// </summary>
    public sealed class UsbGuard : GuardBase
    {
        public override string Name => "USB存储设备";
        public override bool Enabled => Context?.Config.Usb.Enabled ?? false;
        protected override int IntervalMs => 15000;

        private static readonly string[] Keys =
        {
            @"SYSTEM\CurrentControlSet\Services\usbstor",
            @"SYSTEM\ControlSet001\Services\usbstor",
            @"SYSTEM\ControlSet002\Services\usbstor",
            @"SYSTEM\ControlSet003\Services\usbstor"
        };

        private bool _reported;

        protected override void OnStart()
        {
            if (Context.Config.Usb.AllowStorage) { SetStatus("允许使用USB存储设备"); return; }
            Disable();
        }

        protected override void OnTick()
        {
            if (Context.Config.Usb.AllowStorage) { SetStatus("允许使用USB存储设备"); return; }

            object v = SystemActions.GetRegistryValue(RegistryHive.LocalMachine, Keys[0], "Start");
            int start = v == null ? -1 : Convert.ToInt32(v);
            if (start != 4)
            {
                if (!_reported)
                {
                    _reported = true;
                    Context.Report(Name, "检测到 USB 存储设备策略被修改，已重新禁用。", ViolationAction.Notify);
                }
                Disable();
                SetStatus("检测到 usbstor 被改回，已重新禁用");
            }
            else
            {
                SetStatus("USB存储设备已禁用（键鼠不受影响）");
            }
        }

        private void Disable()
        {
            foreach (string k in Keys)
            {
                if (SystemActions.RegistryKeyExists(RegistryHive.LocalMachine, k))
                    SystemActions.SetRegistryValue(RegistryHive.LocalMachine, k, "Start", 4, RegistryValueKind.DWord);
            }
            Log.Info("已禁用 USB 存储设备（usbstor Start=4）");
        }

        protected override void OnStop()
        {
            if (Context == null) return;
            foreach (string k in Keys)
            {
                if (SystemActions.RegistryKeyExists(RegistryHive.LocalMachine, k))
                    SystemActions.SetRegistryValue(RegistryHive.LocalMachine, k, "Start", 3, RegistryValueKind.DWord);
            }
            Log.Info("已恢复 USB 存储设备（usbstor Start=3）");
        }
    }
}
