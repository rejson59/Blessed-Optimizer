using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace BlessedOptimizer.Services;

public sealed record PowerOption(uint Value, string Label);

public sealed record PowerSettingDescriptor(
    string Key,
    string Name,
    string Description,
    Guid SubgroupId,
    Guid SettingId,
    uint MinimumValue,
    uint MaximumValue,
    bool AllowAnyValueInRange,
    IReadOnlyList<PowerOption> Options);

public sealed record PowerRestorePoint(
    Guid SchemeId,
    string SettingKey,
    uint AcValue,
    uint DcValue,
    DateTimeOffset SavedAt);

public sealed record PowerSettingState(
    PowerSettingDescriptor Descriptor,
    uint AcValue,
    uint DcValue,
    PowerRestorePoint? RestorePoint);

public sealed record PowerPlanSnapshot(
    Guid SchemeId,
    string SchemeName,
    IReadOnlyList<PowerSettingState> Settings);

public static class PowerSettingsService
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorInsufficientBuffer = 122;

    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid PcieSubgroup = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    private static readonly IReadOnlyList<PowerSettingDescriptor> Settings = new[]
    {
        new PowerSettingDescriptor(
            "processor-max",
            "Limit procesora",
            "Maksymalny stan procesora w tym planie. Niższy limit może ograniczyć temperaturę i hałas, ale też wydajność; nie wyłącza zabezpieczeń termicznych.",
            ProcessorSubgroup,
            new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"),
            0,
            100,
            true,
            Enumerable.Range(10, 11).Select(index => new PowerOption((uint)(index * 5), $"{index * 5}%")).ToArray()),
        new PowerSettingDescriptor(
            "usb-selective-suspend",
            "Oszczędzanie energii USB",
            "Zezwala Windowsowi na usypianie nieużywanych urządzeń USB. Wyłączenie może pomóc przy rozłączających się urządzeniach, ale zwiększa pobór energii.",
            UsbSubgroup,
            new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
            0,
            1,
            false,
            new[] { new PowerOption(0, "Wyłączone"), new PowerOption(1, "Włączone · oszczędza energię") }),
        new PowerSettingDescriptor(
            "pcie-link-state",
            "Oszczędzanie energii PCI Express",
            "Steruje oszczędzaniem energii łącza PCIe. Mniejsze oszczędzanie może ograniczyć opóźnienie wybudzania, ale skrócić czas pracy na baterii.",
            PcieSubgroup,
            new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5"),
            0,
            2,
            false,
            new[]
            {
                new PowerOption(0, "Wyłączone · maks. responsywność"),
                new PowerOption(1, "Umiarkowane oszczędzanie"),
                new PowerOption(2, "Maksymalne oszczędzanie")
            })
    };

    private static readonly string BackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlessedOptimizer",
        "power-settings-backups.json");

    public static IReadOnlyList<PowerSettingDescriptor> SupportedSettings => Settings;

    public static PowerPlanSnapshot ReadActivePlan()
    {
        var schemeId = GetActiveScheme();
        var schemeName = ReadFriendlyName(schemeId);
        var backups = ReadRestorePoints();
        var states = new List<PowerSettingState>();

        foreach (var descriptor in Settings)
        {
            try
            {
                var (ac, dc) = ReadValues(schemeId, descriptor);
                var restorePoint = backups.FirstOrDefault(point =>
                    point.SchemeId == schemeId && string.Equals(point.SettingKey, descriptor.Key, StringComparison.Ordinal));
                states.Add(new PowerSettingState(descriptor, ac, dc, restorePoint));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 1168 or 87)
            {
                // Windows can omit a setting that is not supported by this device or power plan.
            }
        }

        return new PowerPlanSnapshot(schemeId, schemeName, states);
    }

    public static bool SaveOriginalIfNeeded(PowerSettingState state, Guid schemeId)
    {
        var points = ReadRestorePoints();
        if (points.Any(point => point.SchemeId == schemeId && point.SettingKey == state.Descriptor.Key))
            return false;

        points.Add(new PowerRestorePoint(schemeId, state.Descriptor.Key, state.AcValue, state.DcValue, DateTimeOffset.Now));
        WriteRestorePoints(points);
        return true;
    }

    public static PowerRestorePoint? FindRestorePoint(Guid schemeId, string settingKey) =>
        ReadRestorePoints().FirstOrDefault(point => point.SchemeId == schemeId && point.SettingKey == settingKey);

    public static void RemoveRestorePoint(Guid schemeId, string settingKey)
    {
        var points = ReadRestorePoints();
        var remaining = points.Where(point => point.SchemeId != schemeId || point.SettingKey != settingKey).ToList();
        if (remaining.Count != points.Count)
            WriteRestorePoints(remaining);
    }

    public static void ApplyFromElevatedHelper(string schemeText, string settingKey, string acText, string dcText, string expectedUserSid)
    {
        string? currentUserSid;
        using (var identity = WindowsIdentity.GetCurrent())
            currentUserSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(expectedUserSid) || !string.Equals(currentUserSid, expectedUserSid, StringComparison.Ordinal))
            throw new InvalidOperationException("UAC uruchomił innego użytkownika. Dla bezpieczeństwa nie zmieniono planu zasilania innego konta.");

        if (!Guid.TryParse(schemeText, out var schemeId) ||
            !uint.TryParse(acText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var acValue) ||
            !uint.TryParse(dcText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var dcValue))
            throw new ArgumentException("Nieprawidłowe parametry ustawienia zasilania.");

        var descriptor = GetDescriptor(settingKey);
        ValidateValue(descriptor, acValue);
        ValidateValue(descriptor, dcValue);
        if (GetActiveScheme() != schemeId)
            throw new InvalidOperationException("Aktywny plan zasilania zmienił się. Odśwież kartę i zatwierdź zmianę ponownie.");
        ApplyValues(schemeId, descriptor, acValue, dcValue);
    }

    private static PowerSettingDescriptor GetDescriptor(string key) =>
        Settings.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))
        ?? throw new ArgumentException("To ustawienie nie znajduje się na liście bezpiecznych opcji Blessed.");

    private static void ValidateValue(PowerSettingDescriptor descriptor, uint value)
    {
        if (value < descriptor.MinimumValue || value > descriptor.MaximumValue ||
            (!descriptor.AllowAnyValueInRange && descriptor.Options.All(option => option.Value != value)))
            throw new ArgumentOutOfRangeException(nameof(value), "Wybrana wartość jest poza dozwolonym zakresem.");
    }

    private static Guid GetActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var schemePointer);
        if (result != ErrorSuccess || schemePointer == IntPtr.Zero)
            throw new Win32Exception((int)result, "Nie udało się odczytać aktywnego planu zasilania.");
        try
        {
            return Marshal.PtrToStructure<Guid>(schemePointer);
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }

    private static string ReadFriendlyName(Guid schemeId)
    {
        uint size = 0;
        var result = PowerReadFriendlyName(IntPtr.Zero, ref schemeId, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if ((result != ErrorMoreData && result != ErrorInsufficientBuffer) || size == 0 || size > 16384)
            return $"Plan {schemeId:D}";

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = PowerReadFriendlyName(IntPtr.Zero, ref schemeId, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
            if (result != ErrorSuccess)
                return $"Plan {schemeId:D}";
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') is { Length: > 0 } name
                ? name
                : $"Plan {schemeId:D}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (uint Ac, uint Dc) ReadValues(Guid schemeId, PowerSettingDescriptor descriptor)
    {
        var subgroup = descriptor.SubgroupId;
        var setting = descriptor.SettingId;
        var acResult = PowerReadACValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, out var acValue);
        if (acResult != ErrorSuccess)
            throw new Win32Exception((int)acResult, $"Nie udało się odczytać ustawienia „{descriptor.Name}” (AC).");

        var dcResult = PowerReadDCValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, out var dcValue);
        if (dcResult != ErrorSuccess)
            throw new Win32Exception((int)dcResult, $"Nie udało się odczytać ustawienia „{descriptor.Name}” (bateria).");

        return (acValue, dcValue);
    }

    private static void ApplyValues(Guid schemeId, PowerSettingDescriptor descriptor, uint acValue, uint dcValue)
    {
        var (oldAc, oldDc) = ReadValues(schemeId, descriptor);
        if (GetActiveScheme() != schemeId)
            throw new InvalidOperationException("Aktywny plan zasilania zmienił się. Nie zapisano ustawienia.");
        var subgroup = descriptor.SubgroupId;
        var setting = descriptor.SettingId;

        try
        {
            CheckPowerResult(PowerWriteACValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, acValue), "AC");
            CheckPowerResult(PowerWriteDCValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, dcValue), "bateria");
            if (GetActiveScheme() != schemeId)
                throw new InvalidOperationException("Aktywny plan zmienił się podczas zapisu. Przywracam poprzednie wartości.");
            CheckPowerResult(PowerSetActiveScheme(IntPtr.Zero, ref schemeId), "zastosowanie planu");
        }
        catch
        {
            // If a write partially succeeded, put both values back before reporting failure.
            _ = PowerWriteACValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, oldAc);
            _ = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeId, ref subgroup, ref setting, oldDc);
            try
            {
                if (GetActiveScheme() == schemeId)
                    _ = PowerSetActiveScheme(IntPtr.Zero, ref schemeId);
            }
            catch (Win32Exception)
            {
                // Keep the original write error; a transient read error must not mask it.
            }
            throw;
        }
    }

    private static void CheckPowerResult(uint result, string operation)
    {
        if (result != ErrorSuccess)
        {
            var reason = result == 5
                ? "Windows odmówił zapisu mimo osobnego monitowania UAC. Sprawdź uprawnienia konta lub zasady urządzenia."
                : $"Nie udało się wykonać operacji: {operation}.";
            throw new Win32Exception((int)result, reason);
        }
    }

    private static List<PowerRestorePoint> ReadRestorePoints()
    {
        if (!File.Exists(BackupFile))
            return new List<PowerRestorePoint>();

        var json = File.ReadAllText(BackupFile);
        return JsonSerializer.Deserialize<List<PowerRestorePoint>>(json) ?? new List<PowerRestorePoint>();
    }

    private static void WriteRestorePoints(List<PowerRestorePoint> points)
    {
        var directory = Path.GetDirectoryName(BackupFile)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"power-settings-backups.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(points));
            File.Move(temporaryPath, BackupFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subgroupGuid,
        IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid settingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid settingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid settingGuid, uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid settingGuid, uint dcValueIndex);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
