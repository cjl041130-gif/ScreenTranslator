using System.Runtime.InteropServices;
using ScreenTranslator.Services;

namespace ScreenTranslator.Capture;

public sealed class MonitorService(AppLogger log)
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        MonitorEnum callback = (nint handle, nint dc, ref Rect rectangle, nint data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
            var found = EnumDisplayDevices(info.Device, 0, ref display, 1);
            var name = found ? display.DeviceString : info.Device;
            // Interface path persists across monitor ordering changes; device name is the fallback.
            var id = found && !string.IsNullOrEmpty(display.DeviceId) ? display.DeviceId : info.Device;
            var scale = GetScaleFactorForMonitor(handle, out var percentage) == 0 ? percentage / 100.0 : 1.0;
            monitors.Add(new(id, name, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top,
                info.Bounds.Left, info.Bounds.Top, scale, (info.Flags & 1) != 0, handle, monitors.Count + 1));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        foreach (var monitor in monitors) log.Info($"Monitor detected: {monitor.DisplayLabel}; id={monitor.Id}");
        return monitors;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx { public int Size; public Rect Bounds, WorkArea; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    private delegate bool MonitorEnum(nint monitor, nint dc, ref Rect rectangle, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
