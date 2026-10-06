using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BlessedOptimizer.Services;

public static class InstallService
{
    public const string ExecutableName = "BlessedOptimizer.exe";
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "BlessedOptimizer");
    public static string InstalledExecutablePath => Path.Combine(InstallDirectory, ExecutableName);
    public static string StartMenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs", "Blessed Optimizer");

    public static bool IsCurrentProcessInstalled()
    {
        var currentPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(currentPath) && PathsEqual(currentPath, InstalledExecutablePath);
    }

    public static bool IsInstalled => File.Exists(InstalledExecutablePath);

    public static void InstallOrUpdate(string sourceExecutablePath, bool createDesktopShortcut)
    {
        if (string.IsNullOrWhiteSpace(sourceExecutablePath) || !File.Exists(sourceExecutablePath))
            throw new FileNotFoundException("Nie udało się znaleźć uruchomionego pliku Blessed Optimizer.", sourceExecutablePath);
        if (IsInstalledCopyRunning())
            throw new InvalidOperationException("Zamknij najpierw uruchomiony Blessed Optimizer, a potem ponów instalację lub aktualizację.");

        Directory.CreateDirectory(InstallDirectory);
        var stagedPath = Path.Combine(InstallDirectory, $"{ExecutableName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceExecutablePath, stagedPath, overwrite: true);
            File.Move(stagedPath, InstalledExecutablePath, overwrite: true);
            CreateShortcut(Path.Combine(StartMenuDirectory, "Blessed Optimizer.lnk"), InstalledExecutablePath);
            if (createDesktopShortcut)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktop, "Blessed Optimizer.lnk"), InstalledExecutablePath);
            }
        }
        finally
        {
            TryDelete(stagedPath);
        }
    }

    public static void LaunchInstalled()
    {
        if (!IsInstalled)
            throw new FileNotFoundException("Nie znaleziono zainstalowanej wersji Blessed Optimizer.", InstalledExecutablePath);
        Process.Start(new ProcessStartInfo(InstalledExecutablePath) { UseShellExecute = true });
    }

    public static bool IsInstalledCopyRunning()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (process.Id != Environment.ProcessId && !string.IsNullOrWhiteSpace(path) && PathsEqual(path, InstalledExecutablePath))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process that cannot expose its executable path is ignored; the file copy will still fail safely if locked.
                }
            }
        }
        return false;
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
        if (shellType is null)
            throw new InvalidOperationException("System Windows nie udostępnił usługi tworzenia skrótów.");

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null)
                throw new InvalidOperationException("Nie udało się przygotować skrótu w menu Start.");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = InstallDirectory;
            shortcut.Description = "Blessed Optimizer — lokalny panel diagnostyczny Windows";
            shortcut.IconLocation = $"{targetPath},0";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject))
                Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
