using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BlessedOptimizer.Models;
using BlessedOptimizer.Services;

namespace BlessedOptimizer;

public partial class InstallerWindow : Window
{
    private readonly bool _alreadyInstalled;
    private readonly DispatcherTimer _factTimer;
    private readonly RotateTransform _progressDoveRotation = new();
    private IReadOnlyList<string> _facts = Array.Empty<string>();
    private int _factIndex;
    private bool _busy;

    public InstallerWindow(bool alreadyInstalled)
    {
        InitializeComponent();
        ProgressDove.RenderTransform = _progressDoveRotation;
        _alreadyInstalled = alreadyInstalled;
        Choice = InstallerChoice.Cancelled;
        _factTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _factTimer.Tick += (_, _) => RotateFact();
    }

    public InstallerChoice Choice { get; private set; }
    public DeviceSnapshot? Snapshot { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_alreadyInstalled)
        {
            InstallButton.Content = "Zaktualizuj lub napraw";
            ExistingButton.Visibility = Visibility.Visible;
            ProgressLabel.Text = "Wykryłem już instalację w Twoim profilu.";
            DetectedSummary.Text = "Możesz otworzyć istniejący program albo zaktualizować go tym plikiem instalacyjnym. Jeśli program jest otwarty, najpierw go zamknij.";
        }
        InstallProgress.Value = 0;
        ProgressValue.Text = "0%";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Read WPF control state on the UI thread; only pass the plain value to the worker.
        var createDesktopShortcut = DesktopShortcutCheck.IsChecked == true;
        _busy = true;
        SetProgress(8, "Przygotowuję bezpieczną instalację dla bieżącego użytkownika…");
        InstallButton.IsEnabled = false;
        PortableButton.IsEnabled = false;
        ExistingButton.IsEnabled = false;
        DesktopShortcutCheck.IsEnabled = false;
        _progressDoveRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-5, 5, TimeSpan.FromSeconds(0.45))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        try
        {
            var sourcePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new InvalidOperationException("Windows nie udostępnił ścieżki uruchomionego instalatora.");

            SetProgress(18, "Odczytuję lokalnie procesor, pamięć, grafikę i sieć…");
            var captureTask = SystemSnapshotService.CaptureAsync();
            await Task.Delay(250);
            SetProgress(34, "Sprawdzam parametry bez uruchamiania benchmarku…");
            Snapshot = await captureTask;
            _facts = Snapshot.GetInstallerFacts();
            _factIndex = 0;
            FactText.Text = _facts[0];
            _factTimer.Start();
            DetectedSummary.Text = $"{Snapshot.ProcessorName} · {Snapshot.LogicalProcessorCount} wątków · {Snapshot.TotalMemoryGb:0.#} GB RAM · {Snapshot.GraphicsAdapters}";

            SetProgress(58, "Kopiuję Blessed do folderu Twojego profilu…");
            await Task.Run(() => InstallService.InstallOrUpdate(sourcePath, createDesktopShortcut));
            SetProgress(92, "Tworzę skrót w menu Start…");
            await Task.Delay(250);
            SetProgress(100, "Gotowe. Uruchamiam Blessed Optimizer…");
            Choice = InstallerChoice.InstallCompleted;
            await Task.Delay(450);
            _busy = false;
            _progressDoveRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _factTimer.Stop();
            _progressDoveRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _busy = false;
            InstallButton.IsEnabled = true;
            PortableButton.IsEnabled = true;
            ExistingButton.IsEnabled = _alreadyInstalled;
            DesktopShortcutCheck.IsEnabled = true;
            SetProgress(0, "Instalacja przerwana. Twój system pozostał w nienaruszonym stanie.");
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — instalacja", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Portable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Choice = InstallerChoice.RunPortable;
        DialogResult = false;
    }

    private void Existing_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Choice = InstallerChoice.LaunchInstalled;
        DialogResult = true;
    }

    private void RotateFact()
    {
        if (_facts.Count == 0) return;
        _factIndex = (_factIndex + 1) % _facts.Count;
        FactText.Text = _facts[_factIndex];
    }

    private void SetProgress(double value, string message)
    {
        InstallProgress.Value = value;
        ProgressValue.Text = $"{value:0}%";
        ProgressLabel.Text = message;
        var travel = Math.Max(0, InstallProgress.ActualWidth - ProgressDove.ActualWidth);
        ProgressDove.Margin = new Thickness(travel * Math.Clamp(value / 100d, 0, 1), 0, 0, 0);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
            e.Cancel = true;
    }
}
