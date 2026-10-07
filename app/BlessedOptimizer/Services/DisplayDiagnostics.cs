using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlessedOptimizer.Services;

public sealed record DisplayModeInfo(int Width, int Height, int CurrentHz, int MaximumHz)
{
    public bool CanGoFaster => MaximumHz > CurrentHz + 1;
}

/// <summary>
/// Windows never mentions that a monitor is running below the refresh rate it supports.
/// Blessed checks it on every sweep.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DisplayDiagnostics
{
    private const int EnumCurrentSettings = -1;

    public static DisplayModeInfo? ReadPrimaryDisplay()
    {
        var current = new DevMode { DeviceName = string.Empty, FormName = string.Empty, Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref current))
            return null;

        var maximum = (int)current.DisplayFrequency;
        var mode = new DevMode { DeviceName = string.Empty, FormName = string.Empty, Size = (ushort)Marshal.SizeOf<DevMode>() };
        for (var index = 0; EnumDisplaySettings(null, index, ref mode); index++)
        {
            if (mode.PelsWidth != current.PelsWidth || mode.PelsHeight != current.PelsHeight)
                continue;
            if (mode.BitsPerPel != current.BitsPerPel)
                continue;
            if (mode.DisplayFrequency > maximum && mode.DisplayFrequency < 1000)
                maximum = (int)mode.DisplayFrequency;
        }

        return new DisplayModeInfo((int)current.PelsWidth, (int)current.PelsHeight, (int)current.DisplayFrequency, maximum);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint ICMMethod;
        public uint ICMIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}
