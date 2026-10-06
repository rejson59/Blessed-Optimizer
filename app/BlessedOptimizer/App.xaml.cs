using System.Windows;
using BlessedOptimizer.Models;
using BlessedOptimizer.Services;

namespace BlessedOptimizer;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--apply-update", StringComparison.Ordinal))
        {
            UpdateService.RunReplacementHelper(e.Args);
            Shutdown();
            return;
        }

        var forcePortable = e.Args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
        var skipUpdateCheck = e.Args.Contains("--skip-update-check", StringComparer.OrdinalIgnoreCase);
        DeviceSnapshot? installerSnapshot = null;

        if (!forcePortable && !InstallService.IsCurrentProcessInstalled())
        {
            var installer = new InstallerWindow(InstallService.IsInstalled);
            MainWindow = installer;
            installer.ShowDialog();
            installerSnapshot = installer.Snapshot;

            switch (installer.Choice)
            {
                case InstallerChoice.InstallCompleted:
                case InstallerChoice.LaunchInstalled:
                    try
                    {
                        InstallService.LaunchInstalled();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Blessed Optimizer", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    Shutdown();
                    return;
                case InstallerChoice.RunPortable:
                    break;
                default:
                    Shutdown();
                    return;
            }
        }

        _instanceMutex = new Mutex(false, "Local\\BlessedOptimizer-v1");
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        if (!_ownsMutex)
        {
            MessageBox.Show("Blessed Optimizer jest już uruchomiony.", "Blessed Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var isPortable = forcePortable || !InstallService.IsCurrentProcessInstalled();
        var mainWindow = new MainWindow(installerSnapshot, isPortable, skipUpdateCheck);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex && _instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

public enum InstallerChoice
{
    Cancelled,
    InstallCompleted,
    LaunchInstalled,
    RunPortable
}
