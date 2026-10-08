using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlessedOptimizer.Services;

/// <summary>
/// A present Plug and Play device. Device instance identifiers are deliberately
/// not exposed or persisted; only friendly labels and the reported problem code
/// are returned to the UI.
/// </summary>
public sealed record PeripheralDevice(string Name, string Category, string Manufacturer, uint? ProblemCode)
{
    public bool HasReportedProblem => ProblemCode is > 0;

    public string StatusLabel => ProblemCode switch
    {
        0 => "Brak zgłoszonego błędu",
        { } code => $"Problem urządzenia · kod {code}",
        _ => "Stan nieznany"
    };
}

/// <summary>
/// Read-only inventory of devices that Windows currently reports as present.
/// SetupAPI's DIGCF_PRESENT flag avoids listing disconnected historical devices.
/// This code never opens a camera, reads input, changes a driver, or writes settings.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PeripheralDiagnostics
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint ErrorNoMoreItems = 259;
    private const uint MaximumDeviceCount = 4096;
    private const uint SpdrpDeviceDescription = 0x00000000;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpManufacturer = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint CrSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<PeripheralDevice> ReadPresentDevices(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Odczyt urządzeń Plug and Play jest dostępny wyłącznie w Windows.");

        var deviceSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (deviceSet == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows nie udostępnił listy podłączonych urządzeń.");

        try
        {
            var devices = new List<PeripheralDevice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint index = 0; index < MaximumDeviceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceInfo = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref deviceInfo))
                {
                    if ((uint)Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                        break;
                    continue;
                }

                var className = ReadDeviceProperty(deviceSet, ref deviceInfo, SpdrpClass);
                var friendlyName = ReadDeviceProperty(deviceSet, ref deviceInfo, SpdrpFriendlyName);
                var description = ReadDeviceProperty(deviceSet, ref deviceInfo, SpdrpDeviceDescription);
                var manufacturer = ReadDeviceProperty(deviceSet, ref deviceInfo, SpdrpManufacturer);
                var name = FirstNonEmpty(friendlyName, description);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var category = ClassifyDevice(className, name);
                if (category is null)
                    continue;

                // Same hardware can appear in more than one generic PnP node. Keep
                // one readable row per category/name/manufacturer without retaining IDs.
                var key = $"{category}\0{name}\0{manufacturer}";
                if (!seen.Add(key))
                    continue;

                devices.Add(new PeripheralDevice(
                    name,
                    category,
                    manufacturer ?? string.Empty,
                    ReadProblemCode(deviceInfo.DeviceInstance)));
            }

            return devices
                .OrderBy(device => CategoryOrder(device.Category))
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(128)
                .ToArray();
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    internal static string? ClassifyDevice(string? className, string? name)
    {
        var deviceClass = className?.Trim() ?? string.Empty;
        var deviceName = name?.Trim() ?? string.Empty;
        if (deviceClass.Length == 0 && deviceName.Length == 0)
            return null;

        var searchableName = deviceName.ToLowerInvariant();
        if (ContainsAny(searchableName, "gamepad", "game pad", "game controller", "kontroler", "controller", "joystick", "dualshock", "dualsense", "xbox", "playstation"))
            return "Kontroler";

        if (deviceClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
            return "Monitor";
        if (deviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase))
            return "Klawiatura";
        if (deviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
            return "Mysz";
        if (deviceClass.Equals("Camera", StringComparison.OrdinalIgnoreCase))
            return "Kamera";
        if (deviceClass.Equals("Image", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(searchableName, "camera", "kamera", "webcam", "web cam", "ir camera") ? "Kamera" : "Obrazowanie";
        if (deviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Sound", StringComparison.OrdinalIgnoreCase))
            return "Dźwięk";

        return null;
    }

    private static uint? ReadProblemCode(uint deviceInstance)
    {
        var result = CM_Get_DevNode_Status(out _, out var problemCode, deviceInstance, 0);
        return result == CrSuccess ? problemCode : null;
    }

    private static string? ReadDeviceProperty(IntPtr deviceSet, ref SpDevInfoData deviceInfo, uint property)
    {
        _ = SetupDiGetDeviceRegistryPropertyW(
            deviceSet,
            ref deviceInfo,
            property,
            out _,
            IntPtr.Zero,
            0,
            out var requiredSize);
        if (requiredSize == 0 || requiredSize > 32768)
            return null;

        var buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            if (!SetupDiGetDeviceRegistryPropertyW(
                    deviceSet,
                    ref deviceInfo,
                    property,
                    out _,
                    buffer,
                    requiredSize,
                    out _))
                return null;

            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0').Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static int CategoryOrder(string category) => category switch
    {
        "Monitor" => 0,
        "Klawiatura" => 1,
        "Mysz" => 2,
        "Kamera" => 3,
        "Dźwięk" => 4,
        "Kontroler" => 5,
        _ => 6
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parentWindow, uint flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegistryType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status", SetLastError = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint deviceInstance, uint flags);
}
