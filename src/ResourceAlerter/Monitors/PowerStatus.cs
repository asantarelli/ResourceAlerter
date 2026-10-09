using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceAlerter.Monitors;

/// <summary>
/// What Windows reports about mains power / battery. A UPS connected by USB (HID) shows up here
/// as a battery, via Microsoft's built-in driver — no vendor software involved. A machine without
/// any battery reports <see cref="BatteryPresent"/> = false.
/// </summary>
public sealed record PowerStatus(
    bool BatteryPresent,
    bool? OnMains,
    int? ChargePercent,
    int? RuntimeMinutes,
    bool Charging,
    byte RawAcLineStatus,
    byte RawBatteryFlag);

public interface IPowerStatusReader
{
    PowerStatus Read();
}

public sealed class WindowsPowerStatusReader : IPowerStatusReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public PowerStatus Read()
    {
        if (!GetSystemPowerStatus(out var s))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // BatteryFlag: 128 = no system battery, 255 = unknown. Either way there is nothing to monitor.
        var batteryPresent = s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0;

        bool? onMains = s.ACLineStatus switch
        {
            1 => true,
            0 => false,
            _ => null, // 255 = unknown
        };

        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;

        // 0xFFFFFFFF = unknown, which is also what Windows reports while on mains power.
        int? runtimeMinutes = s.BatteryLifeTime == uint.MaxValue ? null : (int)Math.Round(s.BatteryLifeTime / 60.0);

        return new PowerStatus(batteryPresent, onMains, percent, runtimeMinutes, (s.BatteryFlag & 8) != 0, s.ACLineStatus, s.BatteryFlag);
    }
}
