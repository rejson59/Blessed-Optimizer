using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BlessedOptimizer.Models;
using BlessedOptimizer.Services;

namespace BlessedOptimizer;

public partial class MainWindow : Window
{
    private readonly bool _isPortable;
    private readonly bool _skipUpdateCheck;
    private readonly DispatcherTimer _watchTimer;
    private readonly PerformanceMonitor _performanceMonitor = new();
    private readonly ProcessOverviewService _processOverviewService = new();
    private readonly DispatcherTimer _processTimer;
    private readonly DispatcherTimer _careTimer;
    private readonly BlessedWatchService _watchService = new();
    private readonly PerformanceMonitor _carePerformance = new();
    private readonly CancellationTokenSource _lifetime = new();
    private BlessedProfile _profile = BlessedProfileStore.Load();
    private WatchReport? _lastReport;
    private bool _careBusy;
    private DateTimeOffset _lastAutoCleanupAt = DateTimeOffset.MinValue;
    private TextBlock? _careStatusText;
    private StackPanel? _careFindingsHost;
    private Button? _careScanButton;
    private Button? _oneClickButton;
    private DeviceSnapshot? _snapshot;
    private string _currentPage = "care";
    private bool _monitoring;
    private bool _checkingUpdates;
    private TextBlock? _cpuValue;
    private TextBlock? _memoryValue;
    private TextBlock? _monitoringStatus;
    private ProgressBar? _cpuBar;
    private ProgressBar? _memoryBar;
    private Button? _watchButton;
    private Button? _pingButton;
    private TextBlock? _pingResult;
    private FirstBlessingReport? _firstBlessingReport;
    private TextBlock? _blessingStatusText;
    private StackPanel? _blessingResultsHost;
    private Button? _blessingRunButton;
    private bool _firstBlessingBusy;
    private IReadOnlyList<PeripheralDevice>? _deviceInventory;
    private bool _deviceInventoryAvailable;
    private string? _deviceInventoryMessage;
    private DisplayModeInfo? _primaryDisplay;
    private TextBlock? _deviceDisplayDetails;
    private TextBlock? _deviceDisplayRecommendation;
    private StackPanel? _deviceInventoryHost;
    private Button? _deviceRefreshButton;
    private bool _deviceRefreshBusy;
    private DataGrid? _processGrid;
    private TextBox? _processSearch;
    private TextBlock? _processStatus;
    private Button? _processCloseButton;
    private Button? _processForceCloseButton;
    private IReadOnlyList<ProcessUsageSnapshot> _processRows = Array.Empty<ProcessUsageSnapshot>();
    private bool _processRefreshBusy;
    private bool _processActionBusy;
    private bool _powerOperationBusy;
    private HwndSource? _windowSource;
    private readonly TranslateTransform _cursorWingsPosition = new();
    private readonly ScaleTransform _guideLogoScale = new(1, 1);
    private readonly ScaleTransform _careBadgeScale = new(1, 1);
    private bool _mouseLeaveTrackingRequested;
    private bool _mouseLeaveTrackingIsNonClient;

    private const int WmMouseMove = 0x0200;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmMouseLeave = 0x02A3;
    private const int WmNcMouseLeave = 0x02A2;
    private const uint TmeLeave = 0x00000002;
    private const uint TmeNonClient = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTrackMouseEvent
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref NativeTrackMouseEvent trackingEvent);

    public MainWindow(DeviceSnapshot? initialSnapshot, bool isPortable, bool skipUpdateCheck)
    {
        InitializeComponent();
        CursorWingsAnchor.RenderTransform = _cursorWingsPosition;
        GuideLogo.RenderTransformOrigin = new Point(0.5, 0.5);
        GuideLogo.RenderTransform = _guideLogoScale;
        CareBadge.RenderTransformOrigin = new Point(0.5, 0.5);
        CareBadge.RenderTransform = _careBadgeScale;
        _snapshot = initialSnapshot;
        _isPortable = isPortable;
        _skipUpdateCheck = skipUpdateCheck;
        VersionText.Text = $"v{GetCurrentVersion()}";
        InstallModeText.Text = _isPortable ? "Tryb przenośny" : "Instalacja dla bieżącego użytkownika";
        HeaderSummary.Text = _snapshot is null
            ? "Blessed odczytuje podstawowe parametry lokalnie"
            : $"{_snapshot.OperatingSystem} · {_snapshot.TotalMemoryGb:0.#} GB RAM";
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += WatchTimer_Tick;
        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += ProcessTimer_Tick;
        _careTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _careTimer.Tick += CareTimer_Tick;
        _currentPage = _profile.FirstBlessingCompletedAt is null ? "blessing" : "care";
        UpdateNavigationState();
        RenderCurrentPage();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateCursorWings();
    }

    // Observe both client and non-client movement so WindowChrome's title bar stays in sync too.
    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmMouseMove:
            case WmNcMouseMove:
                if (!IsActive || WindowState == WindowState.Minimized)
                {
                    HideCursorWings();
                    break;
                }
                RequestMouseLeaveTracking(hwnd, message == WmNcMouseMove);
                UpdateCursorWings();
                break;

            case WmMouseLeave:
            case WmNcMouseLeave:
                _mouseLeaveTrackingRequested = false;
                UpdateCursorWings();
                break;
        }

        return IntPtr.Zero;
    }

    private void RequestMouseLeaveTracking(IntPtr hwnd, bool nonClient)
    {
        if (_mouseLeaveTrackingRequested && _mouseLeaveTrackingIsNonClient == nonClient)
            return;

        var trackingEvent = new NativeTrackMouseEvent
        {
            Size = (uint)Marshal.SizeOf<NativeTrackMouseEvent>(),
            Flags = TmeLeave | (nonClient ? TmeNonClient : 0),
            Window = hwnd,
            HoverTime = 0
        };
        _mouseLeaveTrackingRequested = TrackMouseEvent(ref trackingEvent);
        _mouseLeaveTrackingIsNonClient = nonClient;
    }

    private bool UpdateCursorWings()
    {
        if (_windowSource is null || !IsLoaded || !IsActive || WindowState == WindowState.Minimized ||
            !GetCursorPos(out var pointer) || !GetWindowRect(_windowSource.Handle, out var bounds) ||
            pointer.X < bounds.Left || pointer.X >= bounds.Right || pointer.Y < bounds.Top || pointer.Y >= bounds.Bottom)
        {
            HideCursorWings();
            return false;
        }

        Point position;
        try
        {
            position = CursorWingsOverlay.PointFromScreen(new Point(pointer.X, pointer.Y));
        }
        catch (InvalidOperationException)
        {
            HideCursorWings();
            return false;
        }

        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            HideCursorWings();
            return false;
        }

        // Match the site's viewBox offsets; render-transform updates avoid a layout pass and keep the native arrow tip clear.
        _cursorWingsPosition.X = position.X - 34;
        _cursorWingsPosition.Y = position.Y - 20;
        CursorWingsOverlay.Visibility = Visibility.Visible;
        return true;
    }

    private void HideCursorWings() => CursorWingsOverlay.Visibility = Visibility.Collapsed;

    private void Window_Activated(object? sender, EventArgs e) => UpdateCursorWings();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _mouseLeaveTrackingRequested = false;
        HideCursorWings();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => UpdateCursorWings();

    private void Window_MouseLeave(object sender, MouseEventArgs e) => UpdateCursorWings();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateCursorWings();
        if (_snapshot is null)
        {
            FooterStatusText.Text = "Odczytuję podstawowe parametry urządzenia lokalnie…";
            try
            {
                _snapshot = await SystemSnapshotService.CaptureAsync(_lifetime.Token);
                HeaderSummary.Text = $"{_snapshot.OperatingSystem} · {_snapshot.TotalMemoryGb:0.#} GB RAM";
                FooterStatusText.Text = "Przegląd komputera zakończony";
                RenderCurrentPage();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                FooterStatusText.Text = "Część parametrów jest jeszcze odczytywana. Reszta panelu działa normalnie.";
                UpdateStatusText.Text = ex.Message;
            }
        }

        await RunWatchAsync(auto: true);
        if (_profile.WatchInBackground)
            _careTimer.Start();

        if (!_skipUpdateCheck)
            await CheckForUpdatesAsync(showUpToDateMessage: false);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var nextPage = button.Name switch
        {
            nameof(BlessingNavButton) => "blessing",
            nameof(CareNavButton) => "care",
            nameof(DevicesNavButton) => "devices",
            nameof(ProcessesNavButton) => "processes",
            nameof(StartupNavButton) => "startup",
            nameof(CleanupNavButton) => "cleanup",
            nameof(PowerNavButton) => "power",
            nameof(ConnectionsNavButton) => "connections",
            nameof(ProposalsNavButton) => "proposals",
            nameof(PersonalizationNavButton) => "personalization",
            nameof(SettingsNavButton) => "settings",
            nameof(HistoryNavButton) => "history",
            nameof(GamingNavButton) => "gaming",
            _ => "care"
        };
        if (nextPage != "gaming" && _monitoring)
            StopMonitoring();
        if (nextPage != "processes")
            _processTimer.Stop();
        _currentPage = nextPage;
        UpdateNavigationState();
        RenderCurrentPage();
    }

    private void ToggleNavigationGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string group)
            return;

        var items = group switch
        {
            "care" => CareNavItems,
            "performance" => PerformanceNavItems,
            "diagnostics" => DiagnosticsNavItems,
            "windows" => WindowsNavItems,
            "appearance" => AppearanceNavItems,
            _ => null
        };
        if (items is null)
            return;

        var expand = items.Visibility != Visibility.Visible;
        if (expand)
        {
            foreach (var otherGroup in new[] { "care", "performance", "diagnostics", "windows", "appearance" })
            {
                if (otherGroup != group)
                    SetNavigationGroupExpanded(otherGroup, false);
            }
        }
        SetNavigationGroupExpanded(group, expand);
    }

    private void SetNavigationGroupExpanded(string group, bool expanded)
    {
        var (button, items, chevron) = group switch
        {
            "care" => (CareGroupButton, CareNavItems, CareGroupChevron),
            "performance" => (PerformanceGroupButton, PerformanceNavItems, PerformanceGroupChevron),
            "diagnostics" => (DiagnosticsGroupButton, DiagnosticsNavItems, DiagnosticsGroupChevron),
            "windows" => (WindowsGroupButton, WindowsNavItems, WindowsGroupChevron),
            "appearance" => (AppearanceGroupButton, AppearanceNavItems, AppearanceGroupChevron),
            _ => ((Button)null!, (StackPanel)null!, (TextBlock)null!)
        };
        if (button is null)
            return;

        items.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        button.Tag = expanded ? "expanded" : null;
        chevron.Text = expanded ? "⌄" : "›";
    }

    private void UpdateNavigationState()
    {
        BlessingNavButton.Tag = _currentPage == "blessing" ? "active" : null;
        CareNavButton.Tag = _currentPage == "care" ? "active" : null;
        DevicesNavButton.Tag = _currentPage == "devices" ? "active" : null;
        GamingNavButton.Tag = _currentPage == "gaming" ? "active" : null;
        ProcessesNavButton.Tag = _currentPage == "processes" ? "active" : null;
        StartupNavButton.Tag = _currentPage == "startup" ? "active" : null;
        CleanupNavButton.Tag = _currentPage == "cleanup" ? "active" : null;
        PowerNavButton.Tag = _currentPage == "power" ? "active" : null;
        ConnectionsNavButton.Tag = _currentPage == "connections" ? "active" : null;
        ProposalsNavButton.Tag = _currentPage == "proposals" ? "active" : null;
        PersonalizationNavButton.Tag = _currentPage == "personalization" ? "active" : null;
        SettingsNavButton.Tag = _currentPage == "settings" ? "active" : null;
        HistoryNavButton.Tag = _currentPage == "history" ? "active" : null;

        var activeGroup = _currentPage switch
        {
            "care" or "history" => "care",
            "gaming" or "processes" => "performance",
            "connections" or "proposals" => "diagnostics",
            "power" or "startup" or "cleanup" => "windows",
            "personalization" or "settings" => "appearance",
            _ => null
        };
        foreach (var group in new[] { "care", "performance", "diagnostics", "windows", "appearance" })
            SetNavigationGroupExpanded(group, group == activeGroup);
    }

    private void RenderCurrentPage()
    {
        UpdateWorkspaceHeader();
        PageHost.Children.Clear();
        var page = _currentPage switch
        {
            "blessing" => BuildFirstBlessingPage(),
            "devices" => BuildDevicesPage(),
            "gaming" => BuildGamingPage(),
            "processes" => BuildProcessesPage(),
            "startup" => BuildStartupPage(),
            "cleanup" => BuildCleanupPage(),
            "power" => BuildPowerPage(),
            "connections" => BuildConnectionsPage(),
            "proposals" => BuildProposalsPage(),
            "personalization" => BuildPersonalizationPage(),
            "settings" => BuildSettingsPage(),
            "history" => BuildHistoryPage(),
            _ => BuildCarePage()
        };
        PageHost.Children.Add(page);
        WorkspaceScrollViewer.ScrollToTop();
        AnimateIn(PageHost, 16);
        if (_currentPage == "processes")
        {
            _processTimer.Start();
            _ = RefreshProcessesAsync();
        }
        else
        {
            _processTimer.Stop();
        }
    }

    private void WorkspaceScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer workspace)
            return;

        // Let a nested control such as the processes table consume the wheel first.
        // If it has reached its edge, continue scrolling the whole page instead of swallowing input.
        var nested = FindNestedScrollViewer(e.OriginalSource as DependencyObject, workspace);
        if (nested is not null && CanScrollInDirection(nested, e.Delta))
            return;
        if (!CanScrollInDirection(workspace, e.Delta))
            return;

        workspace.ScrollToVerticalOffset(workspace.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindNestedScrollViewer(DependencyObject? source, ScrollViewer workspace)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, workspace))
        {
            if (current is ScrollViewer scrollViewer)
                return scrollViewer;

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return null;
    }

    private static bool CanScrollInDirection(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0 : delta < 0 && viewer.VerticalOffset < viewer.ScrollableHeight;

    private void UpdateWorkspaceHeader()
    {
        var page = _currentPage switch
        {
            "blessing" => (Title: "Czas na odnowę", Crumb: "BŁOGOSŁAWIEŃSTWO", Message: _firstBlessingReport is null
                ? "Wybierz swój rytm. Przygotuję plan, który doda komputerowi lekkości."
                : "Twój plan odnowy jest gotowy. Wybierz kroki, które pasują do Ciebie."),
            "devices" => (Title: "Urządzenia", Crumb: "URZĄDZENIA", Message: "Zobacz, które monitory, klawiatury, myszy, kamery, urządzenia audio i kontrolery Windows widzi jako obecne."),
            "gaming" => (Title: "Strefa gracza", Crumb: "STREFA GRACZA", Message: "Włącz czuwanie, aby obserwować użycie procesora i pamięci podczas gry."),
            "connections" => (Title: "Połączenia", Crumb: "POŁĄCZENIA", Message: "Sprawdzę stan kart sieciowych i wykonam test ping. Każdą zmianę zatwierdzasz Ty."),
            "proposals" => (Title: "Propozycje", Crumb: "PROPOZYCJE", Message: "Podpowiem, co warto sprawdzić, i zaprowadzę Cię prosto do właściwych ustawień Windows."),
            "personalization" => (Title: "Wygląd", Crumb: "WYGLĄD", Message: "Dopasuj motyw i kolor Blessed. Ustawienia systemowe Windows otworzysz osobno."),
            "settings" => (Title: "Ustawienia Blessed", Crumb: "USTAWIENIA", Message: "Wybierz swój priorytet i zdecyduj, które zadania Blessed może wykonywać automatycznie."),
            "history" => (Title: "Historia", Crumb: "HISTORIA", Message: "Ostatnie przeglądy są zapisywane wyłącznie na tym komputerze."),
            "processes" => (Title: "Procesy", Crumb: "PROCESY", Message: "Pokażę zużycie CPU i pamięci przez każdy proces — czytelnie i na żywo."),
            "startup" => (Title: "Autostart", Crumb: "AUTOSTART", Message: "Przejrzyj wpisy autostartu swojego konta. Każda zmiana ma zapisaną kopię do przywrócenia."),
            "cleanup" => (Title: "Porządki", Crumb: "PORZĄDKI", Message: "Przejrzyj aplikacje swojego konta i odinstaluj te, których nie używasz. Ponowna instalacja zależy od dostępności u wydawcy lub w Microsoft Store."),
            "power" => (Title: "Zasilanie", Crumb: "ZASILANIE", Message: "Dostrój plan zasilania. Każdą zmianę potwierdzasz Ty i zawsze możesz ją cofnąć."),
            _ => (Title: "Blessed czuwa", Crumb: "BLESSED CZUWA", Message: _lastReport is null
                ? "Robię przegląd Twojego komputera i zaraz powiem, czym się zająć."
                : _lastReport.Headline)
        };

        WorkspaceKicker.Text = _currentPage == "blessing"
            ? "TWOJE BŁOGOSŁAWIEŃSTWO · DANE LOKALNE"
            : _isPortable ? "TRYB PRZENOŚNY · DANE LOKALNE" : "TWÓJ PANEL · DANE NA ŻYWO";
        WorkspaceTitle.Text = page.Title;
        CrumbTitle.Text = page.Crumb;
        BlessedMessage.Text = page.Message;
        WorkspaceStatusText.Text = _snapshot is null ? "Odczytuję komputer" : "Blessed jest gotowy";
    }

    private static string FormatPolishCount(int count, string singular, string pluralFew, string pluralMany)
    {
        var absolute = Math.Abs((long)count);
        var lastTwoDigits = absolute % 100;
        var lastDigit = absolute % 10;
        var form = lastTwoDigits is >= 11 and <= 14
            ? pluralMany
            : lastDigit == 1
                ? singular
                : lastDigit is >= 2 and <= 4
                    ? pluralFew
                    : pluralMany;
        return $"{count} {form}";
    }

    private UIElement BuildFirstBlessingPage()
    {
        var page = NewPage("BLESSED · CZAS NA ODNOWĘ", "Twój komputer znów może złapać oddech.", iconKey: "IconSpark", description:
            "Wybierz swój rytm. Blessed przygotuje osobisty plan, który pomoże Twojemu komputerowi działać tak, jak lubisz.");

        var priority = NewPanel();
        priority.Children.Add(PanelHeadingWithHelp("IconSpark", "Wybierz swój rytm", "Cel pomaga Blessed uporządkować wskazówki i przygotować propozycje. Sam wybór nie zmienia ustawień Windows ani nie zamyka programów.", "AccentBrush", 14));
        priority.Children.Add(Text("Granie, praca, cisza czy dłuższa bateria? Dopasuję do tego kolejność wskazówek.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 10)));
        priority.Children.Add(BuildPriorityChoices(completeOnboarding: true));
        page.Children.Add(WrapPanel(priority));

        var scan = NewPanel();
        scan.Children.Add(Text("Gotowy na swój pierwszy rytuał?", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        scan.Children.Add(Text("Za chwilę sprawdzę, gdzie można dodać komputerowi lekkości, i ułożę plan do Twojej decyzji.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 11)));
        var actions = new DockPanel { LastChildFill = false };
        _blessingStatusText = Text(_firstBlessingReport is not null
            ? $"Plan odnowy: {_firstBlessingReport.CompletedAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.GetCultureInfo("pl-PL"))} · {_firstBlessingReport.Duration.TotalSeconds:0.#} s"
            : _profile.FirstBlessingCompletedAt is { } previousAt
                ? $"Ostatnie błogosławieństwo: {previousAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.GetCultureInfo("pl-PL"))} · możesz przygotować nowy plan."
                : "Twój komputer czeka na pierwszy rytuał.",
            10, "TextSecondaryBrush");
        _blessingStatusText.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(_blessingStatusText);
        _blessingRunButton = new Button
        {
            Content = ButtonContent("IconSearch", _profile.FirstBlessingCompletedAt is null ? "Rozpocznij błogosławieństwo" : "Odśwież plan odnowy", "AccentTextBrush"),
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(13, 8, 13, 8),
            IsEnabled = !_firstBlessingBusy
        };
        _blessingRunButton.Click += async (_, _) => await RunFirstBlessingAsync();
        DockPanel.SetDock(_blessingRunButton, Dock.Right);
        actions.Children.Add(_blessingRunButton);
        scan.Children.Add(actions);
        page.Children.Add(WrapPanel(scan));

        var results = NewPanel();
        results.Children.Add(Text("TWÓJ PLAN ODNOWY", 9, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 10)));
        _blessingResultsHost = new StackPanel();
        results.Children.Add(_blessingResultsHost);
        if (_firstBlessingReport is null)
            _blessingResultsHost.Children.Add(Text(_profile.FirstBlessingCompletedAt is null
                ? "Po błogosławieństwie zobaczysz tutaj odkrycia Blessed i konkretne kroki, które możesz wybrać."
                : "Przygotuj świeży plan, aby zobaczyć aktualne odkrycia dla swojego komputera.",
                11, "TextSecondaryBrush", lineHeight: 18));
        else
            RenderFirstBlessingChecks(_blessingResultsHost, _firstBlessingReport);
        page.Children.Add(WrapPanel(results));

        var details = new Expander
        {
            Header = Text("Szczegóły dla ciekawych", 10, "TextSecondaryBrush", FontWeights.SemiBold),
            IsExpanded = false,
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            Foreground = GetBrush("TextSecondaryBrush")
        };
        var detailPanel = NewPanel();
        detailPanel.Children.Add(PanelHeading("IconShield", "Spokojna odnowa", "GoldBrush", 13));
        detailPanel.Children.Add(Text("Blessed odczytuje informacje udostępnione przez Windows, próbkuje zasoby komputera i proponuje dobrowolne następne kroki. Błogosławieństwo nie otwiera kamery ani mikrofonu i nie rejestruje klawiatury lub myszy. Zmiany systemowe zatwierdzasz osobno.", 10, "TextSecondaryBrush", margin: new Thickness(0, 7, 0, 0), lineHeight: 17));
        detailPanel.Children.Add(Text("Dane odczytu pozostają lokalne. Niezależne sprawdzenie aktualizacji łączy się z GitHub.", 10, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0), lineHeight: 16));
        details.Content = WrapPanel(detailPanel);
        page.Children.Add(details);
        return page;
    }

    private async Task RunFirstBlessingAsync()
    {
        if (_firstBlessingBusy)
            return;

        _firstBlessingBusy = true;
        if (_blessingRunButton is not null)
            _blessingRunButton.IsEnabled = false;
        if (_blessingStatusText is not null)
            _blessingStatusText.Text = "Blessed szuka sposobów na odnowę Twojego komputera…";
        FooterStatusText.Text = "Blessed przygotowuje Twój plan odnowy";
        SetScanningIndicator(true);

        try
        {
            var report = await FirstBlessingService.RunAsync(_profile.Priority, _lifetime.Token);
            _firstBlessingReport = report;
            _deviceInventory = report.Devices;
            _deviceInventoryAvailable = report.DeviceInventoryAvailable;
            _deviceInventoryMessage = report.DeviceInventoryMessage;
            _primaryDisplay = report.Display;
            _profile.FirstBlessingCompletedAt = report.CompletedAt;
            BlessedProfileStore.Save(_profile);
            FooterStatusText.Text = "Błogosławieństwo zakończone · plan odnowy gotowy";

            if (_currentPage == "blessing")
            {
                RenderCurrentPage();
                BlessedMessage.Text = "Plan odnowy gotowy. Wybierz kroki, które pasują do Twojego celu.";
            }
        }
        catch (OperationCanceledException)
        {
            if (_currentPage == "blessing" && _blessingStatusText is not null)
                _blessingStatusText.Text = "Możesz wrócić do odnowy, kiedy będziesz gotowy.";
        }
        catch (Exception)
        {
            FooterStatusText.Text = "Blessed przygotuje nowy plan, gdy spróbujesz ponownie";
            if (_currentPage == "blessing" && _blessingStatusText is not null)
                _blessingStatusText.Text = "Nie udało się zebrać wszystkich informacji. Spróbuj ponownie — Blessed nadal czeka.";
        }
        finally
        {
            _firstBlessingBusy = false;
            SetScanningIndicator(false);
            if (_blessingRunButton is { IsLoaded: true })
                _blessingRunButton.IsEnabled = true;
        }
    }

    private void RenderFirstBlessingChecks(StackPanel host, FirstBlessingReport report)
    {
        host.Children.Clear();
        var attentionCount = report.Checks.Count(check => check.Status is FirstBlessingStatus.Review or FirstBlessingStatus.Attention);
        var summary = new UniformGrid { Columns = 3, Rows = 1, Margin = new Thickness(0, 0, 0, 8) };
        summary.Children.Add(SpecCard("CZAS ODCZYTU", $"{report.Duration.TotalSeconds:0.#} s", "IconClock"));
        summary.Children.Add(SpecCard("WSKAZÓWKI", attentionCount.ToString(CultureInfo.CurrentCulture), "IconSpark"));
        summary.Children.Add(SpecCard("ZMIANY SYSTEMOWE", "0", "IconShield"));
        host.Children.Add(summary);

        foreach (var check in report.Checks)
        {
            var card = NewPanel();
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            var (iconKey, color, statusLabel) = check.Status switch
            {
                FirstBlessingStatus.Ready => ("IconCheck", "#79DDB5", "GOTOWE"),
                FirstBlessingStatus.Review => ("IconSpark", "#E5BC67", "WARTO SPRAWDZIĆ"),
                FirstBlessingStatus.Attention => ("IconAlert", "#F2757F", "UWAGA"),
                _ => ("IconAlert", "#A9BED2", "BRAK ODCZYTU")
            };
            var badge = SeverityBadge(iconKey, color, 30);
            badge.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(badge, Dock.Left);
            header.Children.Add(badge);
            var status = Text(statusLabel, 8, check.Status switch
            {
                FirstBlessingStatus.Ready => "AccentBrush",
                FirstBlessingStatus.Review or FirstBlessingStatus.Attention => "GoldBrush",
                _ => "TextSecondaryBrush"
            }, FontWeights.Bold);
            status.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(status, Dock.Right);
            header.Children.Add(status);
            header.Children.Add(Text(check.Title, 13, "TextPrimaryBrush", FontWeights.SemiBold));
            card.Children.Add(header);
            card.Children.Add(Text(check.Detail, 10, "TextSecondaryBrush", lineHeight: 16));
            if (!string.IsNullOrWhiteSpace(check.ActionPage))
            {
                var action = new Button
                {
                    Content = check.ActionPage switch
                    {
                        "devices" => "Otwórz urządzenia",
                        "power" => "Przejrzyj zasilanie",
                        "cleanup" => "Otwórz porządki",
                        "processes" => "Zobacz procesy",
                        "connections" => "Otwórz połączenia",
                        _ => "Zobacz więcej"
                    },
                    Style = (Style)FindResource("SecondaryButton"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 9, 0, 0),
                    Padding = new Thickness(11, 6, 11, 6)
                };
                var targetPage = check.ActionPage;
                action.Click += (_, _) => NavigateTo(targetPage);
                card.Children.Add(action);
            }
            host.Children.Add(WrapPanel(card));
        }
    }

    private UIElement BuildDevicesPage()
    {
        var page = NewPage("URZĄDZENIA · LOKALNY ODCZYT", "Sprawdź, co Windows naprawdę widzi.", iconKey: "IconDisplay", description:
            "Lista obecnych urządzeń Plug and Play, nie historycznych wpisów. Blessed pokazuje stan zgłaszany przez Windows i prowadzi do właściwych ustawień — nie zmienia sterowników ani konfiguracji.");

        var display = NewPanel();
        display.Children.Add(PanelHeading("IconDisplay", "Ekran główny", "AccentBrush", 14));
        _deviceDisplayDetails = Text(string.Empty, 11, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 8, 0, 0));
        _deviceDisplayRecommendation = Text(string.Empty, 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 10));
        UpdateDeviceDisplaySummary();
        display.Children.Add(_deviceDisplayDetails);
        display.Children.Add(_deviceDisplayRecommendation);
        var displayActions = new WrapPanel();
        displayActions.Children.Add(ChoiceButton("Ustawienia ekranu", () => OpenWindowsSettings("ms-settings:display")));
        displayActions.Children.Add(ChoiceButton("Zaawansowane ustawienia ekranu", () => OpenWindowsSettings("ms-settings:display-advanced")));
        display.Children.Add(displayActions);
        page.Children.Add(WrapPanel(display));

        var inventory = NewPanel();
        var toolbar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var heading = Text("Urządzenia obecne", 15, "TextPrimaryBrush", FontWeights.SemiBold);
        heading.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(heading);
        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var manager = new Button
        {
            Content = "Menedżer urządzeń",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        manager.Click += (_, _) => OpenDeviceManager();
        controls.Children.Add(manager);
        _deviceRefreshButton = new Button
        {
            Content = "Odśwież listę",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(11, 7, 11, 7),
            IsEnabled = !_deviceRefreshBusy
        };
        _deviceRefreshButton.Click += async (_, _) => await RefreshDevicesAsync(_deviceInventoryHost!);
        controls.Children.Add(_deviceRefreshButton);
        DockPanel.SetDock(controls, Dock.Right);
        toolbar.Children.Insert(0, controls);
        inventory.Children.Add(toolbar);

        _deviceInventoryHost = new StackPanel();
        inventory.Children.Add(_deviceInventoryHost);
        if (_deviceInventory is not null)
        {
            RenderPeripheralInventory(_deviceInventoryHost);
        }
        else if (_deviceRefreshBusy)
        {
            _deviceInventoryHost.Children.Add(Text("Odczyt urządzeń jest w toku…", 10, "TextSecondaryBrush"));
        }
        else
        {
            _deviceInventoryHost.Children.Add(Text("Odczytuję listę obecnych urządzeń…", 10, "TextSecondaryBrush"));
            _ = RefreshDevicesAsync(_deviceInventoryHost);
        }
        page.Children.Add(WrapPanel(inventory));

        var privacy = NewPanel();
        privacy.Children.Add(PanelHeading("IconShield", "Twoja prywatność", "GoldBrush", 13));
        privacy.Children.Add(Text("Kamera nie jest uruchamiana ani podglądana. Blessed nie odczytuje tekstu z klawiatury, ruchu myszy ani zawartości mikrofonu — widzi wyłącznie nazwy i status urządzeń, które Windows zgłasza jako obecne.", 10, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 10), lineHeight: 16));
        var privacyActions = new WrapPanel();
        privacyActions.Children.Add(ChoiceButton("Prywatność kamery", () => OpenWindowsSettings("ms-settings:privacy-webcam")));
        privacyActions.Children.Add(ChoiceButton("Ustawienia dźwięku", () => OpenWindowsSettings("ms-settings:sound")));
        privacy.Children.Add(privacyActions);
        page.Children.Add(WrapPanel(privacy));
        return page;
    }

    private void UpdateDeviceDisplaySummary()
    {
        if (_deviceDisplayDetails is null || _deviceDisplayRecommendation is null)
            return;

        if (_primaryDisplay is { } mode)
        {
            _deviceDisplayDetails.Text = $"{mode.Width} × {mode.Height} · aktualnie {mode.CurrentHz} Hz · najwyższy odczytany tryb dla tej rozdzielczości: {mode.MaximumHz} Hz";
            _deviceDisplayRecommendation.Text = mode.CanGoFaster
                ? "Windows udostępnia wyższą częstotliwość. Możesz wybrać ją ręcznie — Blessed nie przełącza trybu ekranu."
                : "Nie znaleziono wyższego odświeżania dla bieżącej rozdzielczości i głębi koloru.";
            _deviceDisplayDetails.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        }
        else
        {
            _deviceDisplayDetails.Text = "Dane ekranu nie są jeszcze dostępne. Odśwież urządzenia albo sprawdź Ustawienia Windows.";
            _deviceDisplayRecommendation.Text = string.Empty;
            _deviceDisplayDetails.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        }
    }

    private async Task RefreshDevicesAsync(StackPanel host)
    {
        if (_deviceRefreshBusy)
            return;

        _deviceRefreshBusy = true;
        if (_deviceRefreshButton is not null)
            _deviceRefreshButton.IsEnabled = false;
        host.Children.Clear();
        host.Children.Add(Text("Odczytuję urządzenia widoczne dla Windows…", 10, "TextSecondaryBrush"));
        try
        {
            var result = await Task.Run(() => new DevicePageReadResult(
                PeripheralDiagnostics.ReadPresentDevices(_lifetime.Token),
                DisplayDiagnostics.ReadPrimaryDisplay()), _lifetime.Token);
            _deviceInventory = result.Devices;
            _deviceInventoryAvailable = true;
            _deviceInventoryMessage = null;
            _primaryDisplay = result.Display;
            FooterStatusText.Text = $"Odczyt urządzeń zakończony · {result.Devices.Count} pozycji · tylko do odczytu";
            if (_currentPage == "devices")
            {
                UpdateDeviceDisplaySummary();
                if (_deviceInventoryHost is not null)
                    RenderPeripheralInventory(_deviceInventoryHost);
            }
        }
        catch (OperationCanceledException)
        {
            if (_currentPage == "devices")
                host.Children.Clear();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            _deviceInventory = Array.Empty<PeripheralDevice>();
            _deviceInventoryAvailable = false;
            _deviceInventoryMessage = ex.Message;
            if (_currentPage == "devices")
            {
                UpdateDeviceDisplaySummary();
                if (_deviceInventoryHost is not null)
                    RenderPeripheralInventory(_deviceInventoryHost);
            }
        }
        finally
        {
            _deviceRefreshBusy = false;
            if (_deviceRefreshButton is { IsLoaded: true })
                _deviceRefreshButton.IsEnabled = true;
        }
    }

    private void RenderPeripheralInventory(StackPanel host)
    {
        host.Children.Clear();
        if (!_deviceInventoryAvailable)
        {
            host.Children.Add(Text($"Nie udało się odczytać obecnych urządzeń: {_deviceInventoryMessage ?? "brak szczegółów"}", 10, "TextSecondaryBrush", lineHeight: 16));
            return;
        }

        var devices = _deviceInventory ?? Array.Empty<PeripheralDevice>();
        var counts = devices
            .GroupBy(device => device.Category, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToArray();
        host.Children.Add(Text(devices.Count == 0
            ? "Nie znaleziono urządzeń w obsługiwanych kategoriach. Część sprzętu może być opisana przez sterownik inną nazwą."
            : $"Wykryto {FormatPolishCount(devices.Count, "urządzenie", "urządzenia", "urządzeń")}{(counts.Length == 0 ? string.Empty : " · " + string.Join(" · ", counts))}.",
            10, "TextSecondaryBrush", margin: new Thickness(0, 0, 0, 10)));

        foreach (var group in devices.GroupBy(device => device.Category, StringComparer.CurrentCultureIgnoreCase))
        {
            var category = NewPanel();
            category.Children.Add(Text(group.Key.ToUpperInvariant(), 9, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 9)));
            foreach (var device in group)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var copy = new StackPanel();
                copy.Children.Add(Text(device.Name, 11, "TextPrimaryBrush", FontWeights.SemiBold));
                if (!string.IsNullOrWhiteSpace(device.Manufacturer))
                    copy.Children.Add(Text(device.Manufacturer, 9, "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 0)));
                row.Children.Add(copy);
                var status = Text(device.StatusLabel, 9, device.HasReportedProblem ? "GoldBrush" : device.ProblemCode == 0 ? "AccentBrush" : "TextSecondaryBrush", FontWeights.SemiBold);
                status.VerticalAlignment = VerticalAlignment.Center;
                status.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(status, 1);
                row.Children.Add(status);
                category.Children.Add(row);
            }
            host.Children.Add(WrapPanel(category));
        }

        if (devices.Any(device => device.HasReportedProblem))
            host.Children.Add(Text("Kod problemu pochodzi z Menedżera urządzeń. Blessed nie próbuje resetować sprzętu ani reinstalować sterownika.", 9, "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 0), lineHeight: 15));
    }

    private static void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            MessageBox.Show($"Nie udało się otworzyć Menedżera urządzeń.\n\n{ex.Message}", "Blessed Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private sealed record DevicePageReadResult(IReadOnlyList<PeripheralDevice> Devices, DisplayModeInfo? Display);

    private UIElement BuildGamingPage()
    {
        var page = NewPage("STREFA GRACZA · DANE LOKALNE", "Razem po spokojniejszą rozgrywkę.", iconKey: "IconGame", description:
            "Monitoruj użycie procesora i pamięci podczas gry. Czuwanie włączasz i zatrzymujesz jednym kliknięciem.");

        var monitor = NewPanel();
        monitor.Children.Add(Text("Tryb czuwania Blessed", 17, "TextPrimaryBrush", FontWeights.SemiBold));
        monitor.Children.Add(Text("Pomiar startuje dopiero po Twoim kliknięciu i odświeża się co 2 sekundy. Zatrzymaj go w dowolnym momencie.", 12, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 14)));

        var controls = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        _monitoringStatus = Text("Czuwanie wyłączone", 11, "TextSecondaryBrush");
        _monitoringStatus.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(_monitoringStatus);
        _watchButton = new Button { Content = "Włącz czuwanie", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(12, 0, 0, 0) };
        _watchButton.Click += ToggleMonitoring_Click;
        DockPanel.SetDock(_watchButton, Dock.Right);
        controls.Children.Add(_watchButton);
        monitor.Children.Add(controls);

        var meters = new UniformGrid { Columns = 2, Rows = 1, Margin = new Thickness(0, 0, 0, 2) };
        meters.Children.Add(BuildUsageMeter("UŻYCIE CPU · CAŁY SYSTEM", out _cpuValue, out _cpuBar));
        meters.Children.Add(BuildUsageMeter("ZAJĘTA PAMIĘĆ RAM", out _memoryValue, out _memoryBar));
        monitor.Children.Add(meters);
        monitor.Children.Add(Text("Przy dużym obciążeniu od razu zobaczysz, gdzie ucieka moc. Procesy systemowe, zabezpieczenia, sterowniki i aktualizacje zostają nietknięte.", 11, "TextSecondaryBrush", margin: new Thickness(0, 13, 0, 0)));
        page.Children.Add(WrapPanel(monitor));

        var hardware = NewPanel();
        hardware.Children.Add(Text("O TYM URZĄDZENIU", 10, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 12)));
        var specs = new UniformGrid { Columns = 2, Rows = 2 };
        specs.Children.Add(SpecCard("PROCESOR", _snapshot?.ProcessorName ?? "Odczyt w toku", "IconCpu"));
        specs.Children.Add(SpecCard("PAMIĘĆ", _snapshot is null ? "—" : $"{_snapshot.TotalMemoryGb:0.#} GB RAM · {_snapshot.AvailableMemoryGb:0.#} GB dostępne", "IconMemory"));
        specs.Children.Add(SpecCard("KARTA GRAFICZNA", _snapshot?.GraphicsAdapters ?? "Odczyt w toku", "IconDisplay"));
        specs.Children.Add(SpecCard("WINDOWS", _snapshot is null ? "—" : $"{_snapshot.OperatingSystem} · kompilacja {_snapshot.OperatingSystemBuild}", "IconShield"));
        hardware.Children.Add(specs);
        if (_snapshot?.SystemDriveFreeGb is { } freeGb)
            hardware.Children.Add(Text($"Dysk systemowy: około {freeGb:0.#} GB wolnego miejsca.", 11, "TextSecondaryBrush", margin: new Thickness(0, 13, 0, 0)));
        page.Children.Add(WrapPanel(hardware));

        var note = NewPanel();
        note.Children.Add(PanelHeading("IconAlert", "Ważne"));
        note.Children.Add(Text("Usługi, zabezpieczenia, sterowniki i Windows Update zostają nietknięte. Autostart Twojego konta i ukryte opcje zasilania zmieniasz świadomie — z opisem skutków i zapisaną kopią do przywrócenia.", 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0)));
        page.Children.Add(WrapPanel(note));
        return page;
    }

    private UIElement BuildProcessesPage()
    {
        var page = NewPage("PROCESY", "Zobacz, co naprawdę zajmuje zasoby.", iconKey: "IconList", description:
            "CPU, pamięć i rola każdego procesu — z krótką podpowiedzią, co można bezpiecznie zamknąć.");
        var panel = NewPanel();
        var toolbar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        _processSearch = new TextBox
        {
            Width = 245,
            Height = 36,
            Padding = new Thickness(10, 6, 10, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderBrush"),
            ToolTip = "Filtruj po nazwie procesu, PID lub roli"
        };
        _processSearch.TextChanged += (_, _) => FilterProcessGrid();
        toolbar.Children.Add(_processSearch);
        var toolbarActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        toolbarActions.Children.Add(HelpButton("CPU pokazuje udział w mocy procesora, a RAM — pamięć aktualnie zajętą przez proces. Rola jest ostrożną wskazówką: Windows, procesy w tle i nieznane pochodzenie są chronione; Blessed nie udaje, że wie, czy zapisałeś pracę."));
        var refresh = new Button { Content = "Odśwież", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(11, 7, 11, 7) };
        refresh.Click += async (_, _) => await RefreshProcessesAsync();
        toolbarActions.Children.Add(refresh);
        DockPanel.SetDock(toolbarActions, Dock.Right);
        toolbar.Children.Add(toolbarActions);
        _processStatus = Text("Przygotowuję pierwszy pomiar…", 10, "TextSecondaryBrush", margin: new Thickness(0, 9, 0, 0));
        DockPanel.SetDock(_processStatus, Dock.Bottom);
        toolbar.Children.Add(_processStatus);
        panel.Children.Add(toolbar);

        _processGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = GetBrush("BorderBrush"),
            Background = GetBrush("SurfaceBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            RowBackground = GetBrush("SurfaceBrush"),
            AlternatingRowBackground = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            MinHeight = 220,
            MaxHeight = 440,
            EnableRowVirtualization = true,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, GetBrush("SurfaceRaisedBrush")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, GetBrush("TextSecondaryBrush")));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        _processGrid.ColumnHeaderStyle = headerStyle;
        _processGrid.Columns.Add(new DataGridTextColumn
        {
            Header = HelpHeader("Proces", "Nazwa aplikacji widocznej dla Windows. Kilka procesów może należeć do jednego programu."),
            Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 115,
            IsReadOnly = true
        });
        _processGrid.Columns.Add(new DataGridTextColumn
        {
            Header = HelpHeader("Rola", "Wskazówka nie zastępuje Twojej decyzji. Windows i Blessed są chronione, procesów bez widocznego okna i nieznanych ścieżek nie zamykam."),
            Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.ImportanceLabel)),
            Width = 145,
            IsReadOnly = true
        });
        var importantColumn = new DataGridCheckBoxColumn
        {
            Header = HelpHeader("Ważny", "Zaznaczenie zapamiętuje nazwę procesu lokalnie w profilu Blessed. Ważne dla Ciebie programy nie będą dostępne do zamknięcia z tego widoku."),
            Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.IsImportant))
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            },
            Width = 68,
            IsReadOnly = false
        };
        _processGrid.Columns.Add(importantColumn);
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.ProcessId)), Width = 58, IsReadOnly = true });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = HelpHeader("CPU", "Udział CPU jest liczony z dwóch lokalnych próbek. Pierwszy odczyt może jeszcze nie mieć wartości."), Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.CpuPercent)) { StringFormat = "{0:0.0}%" }, Width = 68, IsReadOnly = true });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "RAM", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.WorkingSetMb)) { StringFormat = "{0:N0} MB" }, Width = 82, IsReadOnly = true });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "Uruchomiono", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.StartedAt)) { StringFormat = "{0:g}" }, Width = 112, IsReadOnly = true });
        _processGrid.SelectionChanged += (_, _) => UpdateProcessActionButtons();
        _processGrid.CellEditEnding += (_, args) =>
        {
            if (!ReferenceEquals(args.Column, importantColumn) || args.Row.Item is not ProcessUsageSnapshot editedRow)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var row in _processRows.Where(row => string.Equals(row.Name, editedRow.Name, StringComparison.OrdinalIgnoreCase)))
                    row.IsImportant = editedRow.IsImportant;
                SaveImportantProcessNames();
                UpdateProcessActionButtons();
            }));
        };
        panel.Children.Add(_processGrid);

        var processHelp = TextWithHelp(
            "Wybierz w tabeli konkretne aplikacje. Blessed najpierw poprosi je o zwykłe zamknięcie.",
            "Procesy z zaznaczeniem Ważny, składniki Windows, programy działające w tle, inne sesje i procesy o nieznanej ścieżce są wykluczone. Zwykłe zamknięcie może poprosić o zapisanie plików; wymuszenie może utracić niezapisane zmiany.",
            10, "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 9));
        panel.Children.Add(processHelp);
        var processActions = new StackPanel { Orientation = Orientation.Horizontal };
        _processCloseButton = new Button
        {
            Content = "Poproś o zamknięcie",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false
        };
        _processCloseButton.Click += async (_, _) => await RunSelectedProcessActionAsync(forceTerminate: false);
        processActions.Children.Add(_processCloseButton);
        _processForceCloseButton = new Button
        {
            Content = "Wymuś zakończenie…",
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(12, 7, 12, 7),
            IsEnabled = false
        };
        _processForceCloseButton.Click += async (_, _) => await RunSelectedProcessActionAsync(forceTerminate: true);
        processActions.Children.Add(_processForceCloseButton);
        panel.Children.Add(processActions);
        page.Children.Add(WrapPanel(panel));

        var safety = NewPanel();
        safety.Children.Add(PanelHeadingWithHelp(
            "IconCpu", "Ważne dla Ciebie — system chroniony",
            "Blessed nie ocenia przydatności na podstawie samego zużycia CPU lub RAM. Oznacz swoje ważne aplikacje; do zamknięcia trzeba ręcznie zaznaczyć konkretny wiersz. Procesy systemowe, w tle i o nieznanym pochodzeniu zostają zablokowane.",
            "AccentBrush"));
        safety.Children.Add(Text("Najpierw zapisuj ważne pliki. Zwykłe zamknięcie daje aplikacji szansę na zapis; wymuszone kończenie jest osobne, wymaga ponownego potwierdzenia i dotyczy tylko wybranych aplikacji z widocznym oknem.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0), lineHeight: 16));
        page.Children.Add(WrapPanel(safety));
        UpdateProcessActionButtons();
        return page;
    }

    private void SaveImportantProcessNames()
    {
        _profile.ImportantProcessNames = _processRows
            .Where(row => row.IsImportant)
            .Select(row => row.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        BlessedProfileStore.Save(_profile);
    }

    private void UpdateProcessActionButtons()
    {
        if (_processGrid is not null)
            _processGrid.IsEnabled = !_processActionBusy;
        var hasClosableSelection = _processGrid?.SelectedItems
            .OfType<ProcessUsageSnapshot>()
            .Any(row => row.CanClose) == true;
        if (_processCloseButton is not null)
            _processCloseButton.IsEnabled = hasClosableSelection && !_processActionBusy;
        if (_processForceCloseButton is not null)
            _processForceCloseButton.IsEnabled = hasClosableSelection && !_processActionBusy;
    }

    private async Task RunSelectedProcessActionAsync(bool forceTerminate)
    {
        if (_processGrid is null || _processActionBusy)
            return;
        var selectedRows = _processGrid.SelectedItems.OfType<ProcessUsageSnapshot>().ToArray();
        var candidates = selectedRows.Where(row => row.CanClose).ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show(this,
                "Zaznacz w tabeli aplikację z widocznym oknem i upewnij się, że nie oznaczono jej jako ważnej. Składników Windows, procesów w tle i nieznanych procesów Blessed nie zamyka.",
                "Blessed Optimizer — procesy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", candidates.Take(6).Select(row => $"• {row.Name} · PID {row.ProcessId}"));
        if (candidates.Length > 6)
            preview += $"\n• …i jeszcze {candidates.Length - 6} aplikacji";
        var message = forceTerminate
            ? $"Wymusić zakończenie {candidates.Length} zaznaczonych aplikacji?\n\n{preview}\n\nWymuszenie może utracić niezapisane zmiany. Blessed zakończy wyłącznie wskazane procesy — bez ich procesów potomnych. Procesy chronione i oznaczone jako ważne są pomijane."
            : $"Poprosić {candidates.Length} zaznaczonych aplikacji o zwykłe zamknięcie?\n\n{preview}\n\nAplikacje mogą zapytać o zapisanie plików. Jeśli nie odpowiedzą, możesz osobno wybrać wymuszenie zakończenia.";
        if (MessageBox.Show(this, message, forceTerminate ? "Blessed Optimizer — wymuszenie zakończenia" : "Blessed Optimizer — zamknij aplikacje",
                MessageBoxButton.YesNo, forceTerminate ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _processActionBusy = true;
        UpdateProcessActionButtons();
        try
        {
            var results = await Task.Run(() => ProcessControlService.CloseSelected(candidates, forceTerminate));
            var closed = results.Count(result => result.Outcome == ProcessCloseOutcome.Closed);
            var pending = results.Count(result => result.Outcome == ProcessCloseOutcome.StillRunning);
            var changed = results.Count(result => result.Outcome is ProcessCloseOutcome.ProcessChanged or ProcessCloseOutcome.AlreadyExited);
            var failed = results.Count(result => result.Outcome is ProcessCloseOutcome.Failed or ProcessCloseOutcome.Protected);
            var title = forceTerminate ? "Wymuszone zakończenie" : "Zwykłe zamknięcie";
            var summary = $"Zamknięto: {closed}. Nadal działa lub wymaga uwagi: {pending}. Zmienił się stan procesu: {changed}. Pominięto lub nie udało się: {failed}.";
            var details = string.Join("\n", results.Where(result => result.Outcome != ProcessCloseOutcome.Closed).Take(5)
                .Select(result => $"• {result.Name} (PID {result.ProcessId}): {result.Detail}"));
            MessageBox.Show(this, details.Length == 0 ? summary : $"{summary}\n\n{details}",
                $"Blessed Optimizer — {title}", MessageBoxButton.OK,
                failed > 0 || pending > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (_currentPage == "processes")
                await RefreshProcessesAsync();
        }
        finally
        {
            _processActionBusy = false;
            UpdateProcessActionButtons();
        }
    }

    private async Task RefreshProcessesAsync()
    {
        if (_processRefreshBusy || _currentPage != "processes" || _processGrid is null)
            return;
        _processRefreshBusy = true;
        try
        {
            var rows = await Task.Run(_processOverviewService.ReadSnapshot);
            if (_currentPage != "processes" || _processGrid is null)
                return;
            var importantNames = _profile.ImportantProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
                row.IsImportant = importantNames.Contains(row.Name);
            _processRows = rows;
            FilterProcessGrid();
            UpdateProcessActionButtons();
            if (_processStatus is not null)
                _processStatus.Text = $"{rows.Count} procesów · lokalny odczyt {DateTime.Now:HH:mm:ss} · CPU z próbek co 2 s";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            if (_processStatus is not null)
                _processStatus.Text = "Nie udało się odczytać pełnej listy procesów. Odśwież, aby spróbować ponownie.";
        }
        finally
        {
            _processRefreshBusy = false;
        }
    }

    private async void ProcessTimer_Tick(object? sender, EventArgs e) => await RefreshProcessesAsync();

    private void FilterProcessGrid()
    {
        if (_processGrid is null) return;
        var selectedKeys = _processGrid.SelectedItems
            .OfType<ProcessUsageSnapshot>()
            .Where(row => row.CanClose)
            .Select(row => (row.ProcessId, row.StartedAt))
            .ToHashSet();
        var query = _processSearch?.Text.Trim() ?? string.Empty;
        var visibleRows = (string.IsNullOrWhiteSpace(query)
            ? _processRows
            : _processRows.Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                        row.ImportanceLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                        row.ProcessId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)))
            .ToArray();
        _processGrid.ItemsSource = visibleRows;
        foreach (var row in visibleRows)
        {
            if (row.CanClose && selectedKeys.Contains((row.ProcessId, row.StartedAt)))
                _processGrid.SelectedItems.Add(row);
        }
        UpdateProcessActionButtons();
    }

    private UIElement BuildStartupPage()
    {
        var page = NewPage("AUTOSTART", "Wybierz, co startuje razem z Windowsem.", iconKey: "IconRestart", description:
            "Lista obejmuje wpisy Run i RunOnce Twojego konta. Wyłączenie zapisuje kopię i usuwa samą rejestrację autostartu — każdą pozycję przywrócisz jednym kliknięciem.");
        var panel = NewPanel();
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(Text("Pozycje bieżącego konta", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        var refresh = new Button { Content = "Odśwież", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(11, 7, 11, 7) };
        refresh.Click += (_, _) => RenderCurrentPage();
        DockPanel.SetDock(refresh, Dock.Right);
        heading.Children.Insert(0, refresh);
        panel.Children.Add(heading);

        IReadOnlyList<StartupEntry> entries;
        try
        {
            entries = StartupManagerService.ReadEntries();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            panel.Children.Add(Text($"Nie udało się odczytać autostartu bieżącego użytkownika: {ex.Message}", 11, "TextSecondaryBrush"));
            page.Children.Add(WrapPanel(panel));
            return page;
        }

        if (entries.Count == 0)
        {
            panel.Children.Add(Text("Nie znaleziono wpisów w autostarcie bieżącego użytkownika. Wpisy usług i wszystkich użytkowników pozostają nietknięte.", 11, "TextSecondaryBrush"));
        }
        foreach (var entry in entries)
        {
            var item = NewPanel();
            item.Children.Add(Text(entry.Name, 14, "TextPrimaryBrush", FontWeights.SemiBold));
            item.Children.Add(Text(entry.Source, 9, entry.IsEnabled ? "AccentBrush" : "GoldBrush", FontWeights.Bold, new Thickness(0, 3, 0, 0)));
            item.Children.Add(Text(entry.Command, 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 10), lineHeight: 16));
            var toggle = new Button
            {
                Content = entry.IsEnabled ? "Wyłącz autostart" : "Przywróć autostart",
                Style = (Style)FindResource(entry.IsEnabled ? "SecondaryButton" : "PrimaryButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 7, 12, 7)
            };
            toggle.Click += (_, _) => ToggleStartupEntry(entry);
            item.Children.Add(toggle);
            panel.Children.Add(WrapPanel(item));
        }

        var note = NewPanel();
        note.Children.Add(PanelHeading("IconList", "Zakres zmian", fontSize: 12));
        note.Children.Add(Text("Blessed nie wyłącza autostartu maszynowego (HKLM), usług, zadań systemowych, sterowników, Windows Update ani zabezpieczeń. Zmiana dotyczy wyłącznie wpisu konkretnej aplikacji dla zalogowanego konta. Przed wyłączeniem sprawdź polecenie; nie wyłączaj ochrony antywirusowej, kopii zapasowych, synchronizacji, narzędzi dostępności ani sterowników, jeśli nie masz pewności co do skutków.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        panel.Children.Add(WrapPanel(note));
        page.Children.Add(WrapPanel(panel));
        return page;
    }

    private void ToggleStartupEntry(StartupEntry entry)
    {
        var title = entry.IsEnabled ? "Wyłączyć autostart?" : "Przywrócić autostart?";
        var details = entry.IsEnabled
            ? $"Blessed zapisze kopię wpisu i usunie wyłącznie rejestrację startową dla bieżącego konta. Nie zamknie programu ani nie usunie pliku. Nie wyłączaj ochrony antywirusowej, kopii zapasowych, synchronizacji, narzędzi dostępności ani sterowników, jeśli nie rozpoznajesz skutków.\n\n{entry.Name}\n{entry.Command}\n\nKopię można później przywrócić w tej karcie."
            : $"Blessed odtworzy wcześniejszy wpis w autostarcie bieżącego konta. Nie uruchomi teraz programu.\n\n{entry.Name}\n{entry.Command}";
        if (MessageBox.Show(this, details, $"Blessed Optimizer — {title}", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            if (entry.IsEnabled)
                StartupManagerService.Disable(entry);
            else
                StartupManagerService.Restore(entry);
            FooterStatusText.Text = entry.IsEnabled ? "Autostart wyłączono · kopia do przywrócenia została zachowana" : "Autostart przywrócono";
            RenderCurrentPage();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement BuildCleanupPage()
    {
        var page = NewPage("ODCHUDZANIE APLIKACJI", "Więcej miejsca na to, czego używasz.", iconKey: "IconBroom", description:
            "Przejrzyj aplikacje Microsoft Store przypisane do swojego konta. Zaznaczasz każdą pozycję samodzielnie; wspólne i chronione składniki pozostają zablokowane.");
        var host = new StackPanel();
        page.Children.Add(WrapPanel(host));
        host.Children.Add(Text("Wczytuję aplikacje Twojego konta…", 11, "TextSecondaryBrush"));
        _ = LoadCleanupPageAsync(host);
        return page;
    }

    private async Task LoadCleanupPageAsync(StackPanel host)
    {
        IReadOnlyList<AppxPackage> packages;
        try
        {
            packages = await AppxInventoryService.ReadUserPackagesAsync(TimeSpan.FromSeconds(45), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            host.Children.Clear();
            host.Children.Add(TextWithHelp("Nie udało się odczytać aplikacji.", $"Szczegóły odczytu: {ex.Message}", 11, "TextSecondaryBrush"));
            return;
        }

        host.Children.Clear();
        if (packages.Count == 0)
        {
            host.Children.Add(Text("Nie znaleziono aplikacji przypisanych do Twojego konta. Spróbuj odświeżyć stronę Porządki.", 11, "TextSecondaryBrush"));
            return;
        }

        var selectablePackages = packages.Where(package => !package.Safety.IsProtected).ToArray();
        var protectedCount = packages.Count - selectablePackages.Length;
        var boxes = new List<CheckBox>();
        var rows = new Dictionary<CheckBox, FrameworkElement>();
        var appList = NewPanel();
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var counter = Text(string.Empty, 10, "TextSecondaryBrush", FontWeights.SemiBold);
        counter.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(counter);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal };
        headerActions.Children.Add(HelpButton("Ta lista pokazuje identyfikatory aplikacji i wydawców zgłoszone przez Windows. Blessed nie zgaduje, czego już nie używasz: zaznaczasz tylko znane Ci aplikacje. Wspólne frameworki i składniki chronione przez Windows są zablokowane."));
        var selectOptional = new Button { Content = "Zaznacz wszystkie do wyboru", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        var selectNone = new Button { Content = "Wyczyść wybór", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(7, 0, 0, 0) };
        headerActions.Children.Add(selectOptional);
        headerActions.Children.Add(selectNone);
        DockPanel.SetDock(headerActions, Dock.Right);
        header.Children.Add(headerActions);
        appList.Children.Add(header);

        var searchRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var searchHelp = HelpButton("Wyszukiwanie działa w nazwie pakietu i wydawcy. Ukrycie pozycji na liście nie zaznacza jej ani nie usuwa jej z wyboru.");
        DockPanel.SetDock(searchHelp, Dock.Right);
        searchRow.Children.Add(searchHelp);
        var search = new TextBox
        {
            Height = 34,
            Padding = new Thickness(9, 5, 9, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderBrush"),
            ToolTip = "Filtruj po nazwie aplikacji lub wydawcy"
        };
        searchRow.Children.Add(search);
        appList.Children.Add(searchRow);

        foreach (var package in packages.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var safety = package.Safety;
            var box = new CheckBox
            {
                Tag = package,
                IsEnabled = !safety.IsProtected,
                Margin = new Thickness(0, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            var copy = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            copy.Children.Add(Text(package.Name, 10, "TextPrimaryBrush", FontWeights.SemiBold));
            var metadata = new List<string>();
            if (!string.IsNullOrWhiteSpace(package.PublisherDisplayName))
                metadata.Add(package.PublisherDisplayName);
            if (!string.IsNullOrWhiteSpace(package.Version))
                metadata.Add($"v{package.Version}");
            metadata.Add(safety.Category);
            copy.Children.Add(Text(string.Join(" · ", metadata), 8, safety.IsProtected ? "GoldBrush" : "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 0)));
            box.Content = copy;
            box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            boxes.Add(box);

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1), ToolTip = safety.Explanation };
            var help = HelpButton($"{package.Name}\nWydawca: {(string.IsNullOrWhiteSpace(package.PublisherDisplayName) ? "niepodany przez Windows" : package.PublisherDisplayName)}\nWersja: {(string.IsNullOrWhiteSpace(package.Version) ? "brak danych" : package.Version)}\n\n{safety.Explanation}");
            DockPanel.SetDock(help, Dock.Right);
            row.Children.Add(help);
            row.Children.Add(box);
            appList.Children.Add(row);
            rows[box] = row;
        }

        void RefreshCounter()
        {
            var selectedCount = boxes.Count(box => box.IsChecked == true);
            counter.Text = $"{packages.Count} aplikacji · {selectablePackages.Length} do Twojego wyboru · {protectedCount} chronionych · {selectedCount} zaznaczonych";
        }

        void ApplyFilter()
        {
            var query = search.Text.Trim();
            foreach (var pair in rows)
            {
                var package = (AppxPackage)pair.Key.Tag!;
                var matches = string.IsNullOrWhiteSpace(query) ||
                              package.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                              package.PublisherDisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                pair.Value.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        selectOptional.Click += (_, _) => { foreach (var box in boxes.Where(box => box.IsEnabled)) box.IsChecked = true; RefreshCounter(); };
        selectNone.Click += (_, _) => { foreach (var box in boxes) box.IsChecked = false; RefreshCounter(); };
        search.TextChanged += (_, _) => ApplyFilter();
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => RefreshCounter();
            box.Unchecked += (_, _) => RefreshCounter();
        }
        RefreshCounter();

        var uninstall = new Button
        {
            Content = ButtonContent("IconBroom", "Odinstaluj zaznaczone aplikacje", "AccentTextBrush"),
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(13, 8, 13, 8)
        };
        uninstall.Click += async (_, _) =>
        {
            var selected = boxes
                .Where(box => box.IsChecked == true && box.IsEnabled)
                .Select(box => (AppxPackage)box.Tag!)
                .ToArray();
            await RunAppxRemovalAsync(selected);
        };
        appList.Children.Add(uninstall);
        host.Children.Add(WrapPanel(appList));

        var note = NewPanel();
        note.Children.Add(PanelHeadingWithHelp(
            "IconShield", "Odchudzanie z kontrolą",
            "Porządki dotyczą tylko aplikacji AppX bieżącego konta. Nie uruchamiamy skryptów, które wyłączają usługi, zabezpieczenia, sterowniki, zadania Windows lub aktualizacje.",
            "AccentBrush", 12));
        note.Children.Add(Text("Każde odinstalowanie wymaga osobnego potwierdzenia z listą wybranych aplikacji. Blessed chroni pakiety oznaczone jako nieusuwalne, frameworki i komponenty współdzielone. Możliwość ponownej instalacji zależy od dostępności aplikacji u wydawcy lub w Microsoft Store.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0), lineHeight: 16));
        host.Children.Add(WrapPanel(note));
    }

    private async Task RunAppxRemovalAsync(IReadOnlyList<AppxPackage> selected)
    {
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Zaznacz najpierw aplikacje, które chcesz odinstalować.", "Blessed Optimizer — porządki", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", selected.Take(8).Select(package => $"• {package.Name}"));
        if (selected.Count > 8)
            preview += $"\n• …i jeszcze {selected.Count - 8} aplikacji";
        var choice = MessageBox.Show(this,
            $"Odinstalować {selected.Count} zaznaczonych aplikacji bieżącego konta?\n\n{preview}\n\nNie zmieniam usług, sterowników ani składników Windows. Dostępność ponownej instalacji zależy od wydawcy lub Microsoft Store.",
            "Blessed Optimizer — potwierdź odinstalowanie",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
            return;

        FooterStatusText.Text = $"Odinstalowuję {selected.Count} aplikacji…";
        var failed = await AppxInventoryService.RemovePackagesAsync(
            selected,
            progress: null,
            _lifetime.Token);
        _watchService.InvalidateAppxScan();
        _profile.HandledCount++;
        BlessedProfileStore.Save(_profile);

        var removedCount = selected.Count - failed.Count;
        FooterStatusText.Text = failed.Count == 0
            ? $"Odinstalowano {removedCount} aplikacji"
            : $"Odinstalowano {removedCount} aplikacji · {failed.Count} wymagało ręcznej uwagi";
        MessageBox.Show(this,
            failed.Count == 0
                ? $"Gotowe. Odinstalowano {removedCount} aplikacji z Twojego konta. Ponowna instalacja zależy od dostępności aplikacji u wydawcy lub w Microsoft Store."
                : $"Odinstalowano {removedCount} aplikacji. Nie udało się odinstalować {failed.Count} — mogą być używane przez system lub inną sesję.",
            "Blessed Optimizer — porządki", MessageBoxButton.OK,
            failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RenderCurrentPage();
    }

    private UIElement BuildPowerPage()
    {
        var page = NewPage("UKRYTE OPCJE ZASILANIA", "Ustawienia planu bez polowania po Panelu sterowania.", iconKey: "IconBolt", description:
            "Odczytuję prawdziwe wartości aktywnego planu Windows. Zmiany dotyczą osobno zasilania z sieci i baterii, są poprzedzone wyjaśnieniem i zgodą oraz mają zapisaną kopię do cofnięcia.");

        PowerPlanSnapshot snapshot;
        try
        {
            snapshot = PowerSettingsService.ReadActivePlan();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or DllNotFoundException)
        {
            var problem = NewPanel();
            problem.Children.Add(Text("Nie udało się odczytać planu zasilania", 15, "GoldBrush", FontWeights.SemiBold));
            problem.Children.Add(Text(ex.Message, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0)));
            page.Children.Add(WrapPanel(problem));
            return page;
        }

        var summary = NewPanel();
        summary.Children.Add(Text($"Aktywny plan: {snapshot.SchemeName}", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        summary.Children.Add(Text($"GUID planu: {snapshot.SchemeId:D}", 9, "TextSecondaryBrush", margin: new Thickness(0, 4, 0, 0)));
        summary.Children.Add(Text("Zapis ustawień systemowych potwierdzasz w monicie UAC. Limity temperatury, taktowanie i zabezpieczenia pozostają nietknięte.", 10, "GoldBrush", margin: new Thickness(0, 8, 0, 0)));
        page.Children.Add(WrapPanel(summary));

        if (snapshot.Settings.Count == 0)
        {
            var empty = NewPanel();
            empty.Children.Add(Text("Ten plan zasilania nie udostępnia dodatkowych opcji do dostrojenia.", 11, "TextSecondaryBrush"));
            page.Children.Add(WrapPanel(empty));
        }
        foreach (var state in snapshot.Settings)
            page.Children.Add(BuildPowerSettingCard(snapshot, state));

        var note = NewPanel();
        note.Children.Add(PanelHeadingWithHelp("IconRestart", "Cofnięcie zmian", "Pierwsza zmiana zapisuje oryginalne wartości sieci i baterii lokalnie. Użyj przycisku przy danej opcji, aby je przywrócić.", "AccentBrush", 12));
        note.Children.Add(Text("Pierwsza zmiana danej opcji zapisuje oryginalne wartości AC i baterii w profilu użytkownika. Przycisk „Przywróć oryginał” odtwarza te wartości; samo przełączanie profilu nie kasuje kopii.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(note));
        return page;
    }

    private UIElement BuildPowerSettingCard(PowerPlanSnapshot snapshot, PowerSettingState state)
    {
        var panel = NewPanel();
        panel.Children.Add(TextWithHelp(
            state.Descriptor.Name,
            $"{state.Descriptor.Description}\n\nDostępne ustawienia systemowe wyjaśnia Windows. Blessed zmienia wyłącznie tę opcję po Twoim potwierdzeniu.",
            14, "TextPrimaryBrush", FontWeights.SemiBold));
        panel.Children.Add(Text(state.Descriptor.Description, 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 11), lineHeight: 16));

        var controls = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var acLabel = HelpHeader("Zasilanie z sieci", "Limit używany, gdy komputer jest podłączony do zasilania. Wyższe ustawienie może poprawić wydajność, ale zwiększyć zużycie energii i temperaturę.");
        acLabel.Margin = new Thickness(0, 0, 8, 4);
        controls.Children.Add(acLabel);
        var batteryLabel = HelpHeader("Bateria / UPS", "Limit używany z baterii lub UPS-a. Niższa wartość może ograniczyć wydajność i zmniejszyć pobór energii.");
        batteryLabel.Margin = new Thickness(8, 0, 0, 4);
        Grid.SetColumn(batteryLabel, 1);
        controls.Children.Add(batteryLabel);

        var options = state.Descriptor.Options.ToList();
        foreach (var currentValue in new[] { state.AcValue, state.DcValue }.Distinct())
        {
            if (options.All(option => option.Value != currentValue))
                options.Add(new PowerOption(currentValue, $"{currentValue} · bieżąca wartość"));
        }
        var acPicker = CreatePowerPicker(options, state.AcValue);
        Grid.SetRow(acPicker, 1);
        controls.Children.Add(acPicker);
        var dcPicker = CreatePowerPicker(options, state.DcValue);
        Grid.SetRow(dcPicker, 1);
        Grid.SetColumn(dcPicker, 1);
        controls.Children.Add(dcPicker);
        panel.Children.Add(controls);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = new Button { Content = "Zastosuj z potwierdzeniem", Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += async (_, _) =>
        {
            if (acPicker.SelectedValue is uint ac && dcPicker.SelectedValue is uint dc)
                await ApplyPowerSettingAsync(snapshot, state, ac, dc);
        };
        actions.Children.Add(apply);
        var restorePoint = state.RestorePoint;
        if (restorePoint is not null)
        {
            var restore = new Button { Content = "Przywróć oryginał", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(12, 7, 12, 7) };
            restore.Click += async (_, _) => await RestorePowerSettingAsync(restorePoint, state.Descriptor);
            actions.Children.Add(restore);
        }
        panel.Children.Add(actions);
        panel.Children.Add(Text($"Teraz · sieć: {DescribePowerValue(state.Descriptor, state.AcValue)}   |   bateria: {DescribePowerValue(state.Descriptor, state.DcValue)}", 9, "TextSecondaryBrush", margin: new Thickness(0, 8, 0, 0)));
        return WrapPanel(panel);
    }

    private static ComboBox CreatePowerPicker(IReadOnlyList<PowerOption> options, uint selectedValue)
    {
        var picker = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(PowerOption.Label),
            SelectedValuePath = nameof(PowerOption.Value),
            SelectedValue = selectedValue,
            MinWidth = 180,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(7, 4, 7, 4),
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderBrush")
        };
        return picker;
    }

    private async Task ApplyPowerSettingAsync(PowerPlanSnapshot snapshot, PowerSettingState state, uint acValue, uint dcValue)
    {
        if (_powerOperationBusy || (acValue == state.AcValue && dcValue == state.DcValue))
            return;
        var message = $"Zmienić „{state.Descriptor.Name}” w planie „{snapshot.SchemeName}”?\n\nZasilanie z sieci: {DescribePowerValue(state.Descriptor, state.AcValue)} → {DescribePowerValue(state.Descriptor, acValue)}\nBateria / UPS: {DescribePowerValue(state.Descriptor, state.DcValue)} → {DescribePowerValue(state.Descriptor, dcValue)}\n\n{state.Descriptor.Description}\n\nZmiana jest ograniczona do tej opcji planu. Windows pokaże monit UAC; możesz odmówić. Jeśli podasz inne konto administratora, operacja zostanie przerwana, by nie zmienić planu innego użytkownika. Oryginalne wartości zapiszę lokalnie, by dało się je przywrócić.";
        if (MessageBox.Show(this, message, "Blessed Optimizer — potwierdź zmianę", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _powerOperationBusy = true;
        try
        {
            PowerSettingsService.SaveOriginalIfNeeded(state, snapshot.SchemeId);
            var succeeded = await RunElevatedPowerHelperAsync(snapshot.SchemeId, state.Descriptor.Key, acValue, dcValue);
            if (!succeeded)
            {
                // Keep the original snapshot even after a cancelled UAC or partial Windows error; it is safe to restore later.
                RenderCurrentPage();
                return;
            }
            FooterStatusText.Text = $"Zastosowano „{state.Descriptor.Name}” · oryginał można przywrócić";
            RenderCurrentPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — zasilanie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _powerOperationBusy = false;
        }
    }

    private async Task RestorePowerSettingAsync(PowerRestorePoint restorePoint, PowerSettingDescriptor descriptor)
    {
        if (_powerOperationBusy) return;
        var message = $"Przywrócić zapisane oryginalne wartości dla „{descriptor.Name}”?\n\nZasilanie z sieci: {restorePoint.AcValue}\nBateria / UPS: {restorePoint.DcValue}\n\nTo zmieni tylko tę pozycję wskazanego planu. Windows poprosi o zgodę UAC.";
        if (MessageBox.Show(this, message, "Blessed Optimizer — przywracanie", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _powerOperationBusy = true;
        try
        {
            if (!await RunElevatedPowerHelperAsync(restorePoint.SchemeId, descriptor.Key, restorePoint.AcValue, restorePoint.DcValue))
                return;
            PowerSettingsService.RemoveRestorePoint(restorePoint.SchemeId, descriptor.Key);
            FooterStatusText.Text = $"Przywrócono oryginalne wartości: {descriptor.Name}";
            RenderCurrentPage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — przywracanie", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _powerOperationBusy = false;
        }
    }

    private async Task<bool> RunElevatedPowerHelperAsync(Guid schemeId, string settingKey, uint acValue, uint dcValue)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Nie udało się ustalić ścieżki programu do bezpiecznego monitowania UAC.");

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
                throw new InvalidOperationException("Nie udało się ustalić ścieżki aplikacji do bezpiecznego monitowania UAC.");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (!File.Exists(assemblyPath))
                throw new InvalidOperationException("Nie udało się znaleźć aplikacji do bezpiecznego monitowania UAC.");
            startInfo.ArgumentList.Add(assemblyPath);
        }
        startInfo.ArgumentList.Add("--apply-power-setting");
        startInfo.ArgumentList.Add(schemeId.ToString("D"));
        startInfo.ArgumentList.Add(settingKey);
        startInfo.ArgumentList.Add(acValue.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(dcValue.ToString(CultureInfo.InvariantCulture));
        string? userSid;
        using (var identity = WindowsIdentity.GetCurrent())
            userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("Nie udało się zweryfikować bieżącego konta Windows.");
        startInfo.ArgumentList.Add(userSid);
        try
        {
            using var helper = Process.Start(startInfo);
            if (helper is null)
                throw new InvalidOperationException("Windows nie uruchomił pomocnika UAC.");
            await helper.WaitForExitAsync();
            if (helper.ExitCode == 0)
                return true;
            FooterStatusText.Text = "Windows nie zastosował zmiany; sprawdź komunikat pomocnika.";
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FooterStatusText.Text = "Anulowano UAC · ustawienie zasilania nie zostało zmienione";
            return false;
        }
    }

    private static string DescribePowerValue(PowerSettingDescriptor descriptor, uint value) =>
        descriptor.Options.FirstOrDefault(option => option.Value == value)?.Label ??
        (descriptor.Key == "processor-max" ? $"{value}%" : $"Wartość {value}");

    private UIElement BuildConnectionsPage()
    {
        var page = NewPage("POŁĄCZENIA · DANE LOKALNE", "Sprawdzę, co jest nie tak.", iconKey: "IconSignal", description:
            "Lista kart pochodzi bezpośrednio z Windows: Wi-Fi, Ethernet oraz Bluetooth PAN, jeśli system wystawia go jako adapter sieciowy. Test ping wysyła jedno zapytanie do 1.1.1.1 po kliknięciu.");

        var adaptersCard = NewPanel();
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(Text("Karty sieciowe", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        var refresh = new Button { Content = "Odśwież listę", Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(11, 7, 11, 7) };
        refresh.Click += (_, _) =>
        {
            var latestAdapters = SystemSnapshotService.ReadNetworkAdapters();
            if (_snapshot is not null)
                _snapshot = _snapshot with { NetworkAdapters = latestAdapters, CapturedAt = DateTimeOffset.Now };
            RenderCurrentPage();
        };
        DockPanel.SetDock(refresh, Dock.Right);
        heading.Children.Insert(0, refresh);
        adaptersCard.Children.Add(heading);

        var adapters = _snapshot?.NetworkAdapters ?? SystemSnapshotService.ReadNetworkAdapters();
        if (adapters.Count == 0)
        {
            adaptersCard.Children.Add(Text("Nie wykryto aktywnego adaptera. Możesz odświeżyć listę.", 12, "TextSecondaryBrush"));
        }
        else
        {
            for (var adapterIndex = 0; adapterIndex < adapters.Count; adapterIndex++)
            {
                var adapter = adapters[adapterIndex];
                var line = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel();
                left.Children.Add(Text(adapter.Name, 12, "TextPrimaryBrush", FontWeights.SemiBold));
                left.Children.Add(Text($"{adapter.Type} · {adapter.Speed}", 10, "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 0)));
                line.Children.Add(left);
                var status = Text(adapter.Status, 10, adapter.Status == "Połączono" ? "AccentBrush" : "TextSecondaryBrush", FontWeights.SemiBold);
                status.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(status, 1);
                line.Children.Add(status);
                adaptersCard.Children.Add(line);
                if (adapterIndex < adapters.Count - 1)
                {
                    var separator = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) };
                    separator.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                    adaptersCard.Children.Add(separator);
                }
            }
        }
        page.Children.Add(WrapPanel(adaptersCard));

        var testCard = NewPanel();
        testCard.Children.Add(Text("Seria testów stabilności połączenia", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        testCard.Children.Add(Text("Wykonuję 10 krótkich prób do 1.1.1.1 i podaję średnie opóźnienie, jego wahania oraz utracone odpowiedzi. Ping może być blokowany przez zaporę; ustawienia sieci pozostają bez zmian.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 12)));
        var testControls = new DockPanel { LastChildFill = false };
        _pingResult = Text("Test nie został uruchomiony.", 11, "TextSecondaryBrush");
        _pingResult.VerticalAlignment = VerticalAlignment.Center;
        testControls.Children.Add(_pingResult);
        _pingButton = new Button { Content = "Sprawdź ping", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(12, 0, 0, 0) };
        _pingButton.Click += Ping_Click;
        DockPanel.SetDock(_pingButton, Dock.Right);
        testControls.Children.Add(_pingButton);
        testCard.Children.Add(testControls);
        page.Children.Add(WrapPanel(testCard));

        var safety = NewPanel();
        safety.Children.Add(PanelHeading("IconSignal", "Naprawa sieci wymaga Twojej zgody"));
        safety.Children.Add(Text("Blessed diagnozuje połączenie i tłumaczy wynik prostym językiem. Ustawienia sieci pozostają w Twoich rękach.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(safety));
        return page;
    }

    private UIElement BuildProposalsPage()
    {
        var page = NewPage("PROPOZYCJE BLESSED", "Małe rzeczy warte sprawdzenia.", iconKey: "IconSpark", description:
            "Poniższe przyciski prowadzą prosto do właściwej strony Ustawień Windows. Nic nie dzieje się w tle — decyzja należy do Ciebie.");

        if (_snapshot is { TotalMemoryGb: > 0 and < 8 })
            page.Children.Add(ProposalCard("Mało pamięci RAM wykrytej", $"Windows widzi około {_snapshot.TotalMemoryGb:0.#} GB RAM. Zamykaj tylko aplikacje, które sam rozpoznajesz i których teraz nie potrzebujesz. Nie zamykaj procesów systemu ani zabezpieczeń.", "Otwórz wskazówki Windows", null));
        if (_snapshot?.SystemDriveFreeGb is { } freeGb && freeGb < 20)
            page.Children.Add(ProposalCard("Niewiele wolnego miejsca na dysku systemowym", $"Pozostało około {freeGb:0.#} GB. Przejrzyj duże pliki w Ustawieniach Windows i zwolnij miejsce po swojemu.", "Otwórz Czujnik pamięci", "ms-settings:storagesense"));

        page.Children.Add(ProposalCard("Przejrzyj aplikacje uruchamiane z Windowsem", "Wyłącz autostart aplikacji, których nie potrzebujesz od razu po zalogowaniu. Mniej programów na starcie to szybsze wejście do systemu.", "Otwórz Autostart", "ms-settings:startupapps"));
        page.Children.Add(ProposalCard("Sprawdź Tryb gry Windows", "Tryb gry jest ustawieniem systemowym. Przeczytaj opis w Windows i zdecyduj, czy pasuje do Twoich gier.", "Otwórz Tryb gry", "ms-settings:gaming-gamemode"));
        page.Children.Add(ProposalCard("Zadbaj o aktualne kopie ważnych plików", "Przed większymi zmianami systemowymi warto mieć kopię ważnych plików. Blessed pokaże, gdzie ją skonfigurować.", "Otwórz Kopię zapasową", "ms-settings:backup"));
        return page;
    }

    private UIElement BuildPersonalizationPage()
    {
        var page = NewPage("PERSONALIZACJA WINDOWS", "Niech komputer będzie bardziej Twój.", iconKey: "IconPalette", description:
            "Motyw i akcent zmieniają wygląd Blessed Optimizer od razu. Ustawienia systemowe Windows otwierasz osobno.");

        var appThemeCard = NewPanel();
        appThemeCard.Children.Add(Text("Wygląd samego programu", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        appThemeCard.Children.Add(Text("Wybierz motyw aplikacji. Zmiana obowiązuje tylko w bieżącej sesji.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 13)));
        var modes = new WrapPanel();
        modes.Children.Add(ChoiceButton("Ciemny", () => ApplyTheme("dark", null)));
        modes.Children.Add(ChoiceButton("Jasny", () => ApplyTheme("light", null)));
        appThemeCard.Children.Add(modes);
        appThemeCard.Children.Add(PanelHeading("IconPalette", "Kolor akcentu", "TextPrimaryBrush", 12, new Thickness(0, 16, 0, 8)));
        var colors = new WrapPanel();
        colors.Children.Add(ChoiceButton("Błękit", () => ApplyTheme(null, "sky")));
        colors.Children.Add(ChoiceButton("Złoto", () => ApplyTheme(null, "gold")));
        colors.Children.Add(ChoiceButton("Fiolet", () => ApplyTheme(null, "violet")));
        colors.Children.Add(ChoiceButton("Mięta", () => ApplyTheme(null, "mint")));
        appThemeCard.Children.Add(colors);

        var previewCard = NewPanel();
        previewCard.Children.Add(Text("Podgląd ustawień Windows", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        previewCard.Children.Add(Text("Prawdziwe ustawienia systemowe pozostają bez zmian. Jeśli chcesz je obejrzeć, otwórz Ustawienia Windows samodzielnie.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 12)));
        previewCard.Children.Add(PreviewPanel());
        var openSettings = new Button { Content = "Otwórz ustawienia personalizacji Windows", Style = (Style)FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        openSettings.Click += (_, _) => OpenWindowsSettings("ms-settings:personalization");
        previewCard.Children.Add(openSettings);

        var personalizationLayout = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        personalizationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        personalizationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        var controlsPanel = (FrameworkElement)WrapPanel(appThemeCard);
        controlsPanel.Margin = new Thickness(0, 0, 10, 12);
        var previewPanel = (FrameworkElement)WrapPanel(previewCard);
        previewPanel.Margin = new Thickness(0, 0, 0, 12);
        personalizationLayout.Children.Add(controlsPanel);
        Grid.SetColumn(previewPanel, 1);
        personalizationLayout.Children.Add(previewPanel);
        page.Children.Add(personalizationLayout);

        var privacy = NewPanel();
        privacy.Children.Add(PanelHeading("IconShield", "Twoje wybory nie opuszczają aplikacji"));
        privacy.Children.Add(Text("Cała diagnostyka działa lokalnie. Żadne odczyty ani ustawienia wyglądu nie opuszczają Twojego komputera.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(privacy));
        return page;
    }

    // ----- Blessed czuwa: proaktywny przegląd, automatyzacje i priorytety użytkownika -----

    private UIElement BuildCarePage()
    {
        var page = NewPage("BLESSED CZUWA", "Zajmę się tym za Ciebie.", iconKey: "IconShield", description:
            $"Sam sprawdzam dysk, pamięć, autostart, ekran, baterię, zasilanie, aktualizacje i ochronę co 3 minuty, gdy Blessed jest otwarty. {_profile.PriorityPromise}");

        if (!_profile.OnboardingCompleted)
            page.Children.Add(WrapPanel(BuildOnboardingCard()));

        var status = NewPanel();
        var statusBar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 13) };
        var statusCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        statusCopy.Children.Add(Text("STAN OPIEKI", 9, "AccentBrush", FontWeights.Bold));
        _careStatusText = Text(DescribeWatchStatus(), 15, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0));
        statusCopy.Children.Add(_careStatusText);
        statusBar.Children.Add(statusCopy);
        _careScanButton = new Button
        {
            Content = ButtonContent("IconSearch", "Sprawdź teraz", "AccentTextBrush"),
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_careBusy
        };
        _careScanButton.Click += async (_, _) => await RunWatchAsync(auto: false);
        DockPanel.SetDock(_careScanButton, Dock.Right);
        statusBar.Children.Add(_careScanButton);
        _oneClickButton = new Button
        {
            Content = ButtonContent("IconBroom", "Zrób wszystko za mnie", "AccentTextBrush"),
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _oneClickButton.Click += async (_, _) => await RunOneClickOptimizeAsync();
        DockPanel.SetDock(_oneClickButton, Dock.Right);
        statusBar.Children.Add(_oneClickButton);
        status.Children.Add(statusBar);
        status.Children.Add(BuildCareSummaryRow());
        page.Children.Add(WrapPanel(status));

        var findings = NewPanel();
        findings.Children.Add(Text("CO ZNALAZŁEM", 9, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 11)));
        _careFindingsHost = new StackPanel();
        findings.Children.Add(_careFindingsHost);
        page.Children.Add(WrapPanel(findings));
        RenderFindings();

        page.Children.Add(WrapPanel(BuildSettingsShortcutCard()));
        return page;
    }

    private UIElement BuildCareSummaryRow()
    {
        var grid = new UniformGrid { Columns = 3, Rows = 1 };
        grid.Children.Add(SpecCard("OSTATNI PRZEGLĄD", _lastReport is null
            ? "za chwilę"
            : _lastReport.CompletedAt.ToString("HH:mm", CultureInfo.CurrentCulture), "IconClock"));
        grid.Children.Add(SpecCard("ZWOLNIŁEM DLA CIEBIE", _profile.TotalFreedMb >= 1 ? $"{_profile.TotalFreedMb:0} MB" : "jeszcze nic", "IconBroom"));
        grid.Children.Add(SpecCard("CZUWANIE", _profile.WatchInBackground ? "Co 3 minuty · program otwarty" : "Tylko ręcznie", "IconShield"));
        return grid;
    }

    private StackPanel BuildOnboardingCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("ZANIM ZACZNIEMY", 9, "GoldBrush", FontWeights.Bold));
        card.Children.Add(Text("Powiedz mi, co jest dla Ciebie najważniejsze.", 17, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        card.Children.Add(Text("Pod to dobiorę treść podpowiedzi. Automatyczne działania włączysz osobno w Ustawieniach Blessed.", 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 13), lineHeight: 18));
        card.Children.Add(BuildPriorityChoices(completeOnboarding: true));
        return card;
    }

    private StackPanel BuildPriorityCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("TWÓJ RYTM", 9, "AccentBrush", FontWeights.Bold));
        card.Children.Add(Text($"Twój cel: {_profile.PriorityLabel}", 16, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        card.Children.Add(Text(_profile.PriorityPromise, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 12), lineHeight: 18));
        card.Children.Add(BuildPriorityChoices(completeOnboarding: false));
        card.Children.Add(Text("Podpowiedzi i kolejność działań dopasuję do wybranego celu.", 10, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0), lineHeight: 16));
        return card;
    }

    private StackPanel BuildAutomationCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("AUTOMATYCZNE DZIAŁANIA", 9, "AccentBrush", FontWeights.Bold));
        card.Children.Add(Text("Ty wybierasz, co może wydarzyć się bez kolejnego kliknięcia.", 14, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 12)));
        card.Children.Add(AutomationSwitch(
            "Sprawdzaj komputer co 3 minuty, gdy Blessed jest otwarty",
            _profile.WatchInBackground,
            value =>
            {
                _profile.WatchInBackground = value;
                if (value) _careTimer.Start(); else _careTimer.Stop();
            },
            "Gdy Blessed jest otwarty, wykonuje lokalny, tylko-odczytowy przegląd co 3 minuty. Nie uruchamia się przy zamkniętej aplikacji i nie zmienia ustawień Windows."));
        card.Children.Add(TempCleanupAutomationSwitch());
        card.Children.Add(Text("Automatyczne porządki obejmują stare pliki tymczasowe i włączasz je osobno.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0), lineHeight: 16));
        return card;
    }

    private UIElement TempCleanupAutomationSwitch()
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var help = HelpButton("Jeśli włączysz tę opcję i zaakceptujesz monit, Blessed może sprzątać wyłącznie pliki tymczasowe starsze niż 2 dni. Uruchomi się dopiero przy co najmniej 1 GB takich plików, nie częściej niż raz na 12 godzin, i pominie pliki używane przez inne programy.");
        DockPanel.SetDock(help, Dock.Right);
        row.Children.Add(help);
        var box = new CheckBox
        {
            Content = "Automatycznie sprzątaj stare pliki tymczasowe",
            IsChecked = _profile.AllowTempCleanup,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0)
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.Checked += (_, _) => SetTempCleanupConsent(box, enabled: true);
        box.Unchecked += (_, _) => SetTempCleanupConsent(box, enabled: false);
        row.Children.Add(box);
        return row;
    }

    private void SetTempCleanupConsent(CheckBox control, bool enabled)
    {
        if (enabled)
        {
            var choice = MessageBox.Show(this,
                "Blessed może automatycznie usuwać pliki tymczasowe starsze niż 2 dni, ale tylko wtedy, gdy ich łączny rozmiar przekroczy 1 GB. Pliki używane przez inne programy zostaną pominięte. Usuniętych plików nie da się przywrócić.\n\nCzy włączyć automatyczne sprzątanie?",
                "Zgoda na automatyczne sprzątanie",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes)
            {
                control.IsChecked = false;
                return;
            }
        }

        _profile.TempCleanupConsentSet = true;
        _profile.AllowTempCleanup = enabled;
        BlessedProfileStore.Save(_profile);
    }

    private StackPanel BuildMutedCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("WYCISZONE SPRAWY", 9, "AccentBrush", FontWeights.Bold));
        var count = _profile.MutedFindingIds.Count;
        card.Children.Add(Text(count == 0 ? "Nie wyciszyłeś jeszcze żadnej sprawy." : $"Wyciszonych spraw: {count}.", 14, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        card.Children.Add(Text("Wyciszona spraw znika z przeglądu, ale Blessed nadal ją sprawdza. Kliknięcie „✕” na karcie wycisza ją na stałe — aż ją przywrócisz.", 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 12), lineHeight: 18));
        if (count > 0)
        {
            var button = new Button
            {
                Content = ButtonContent("IconCheck", $"Przywróć wszystkie wyciszone ({count})", "TextPrimaryBrush"),
                Style = (Style)FindResource("SecondaryButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(13, 8, 13, 8)
            };
            button.Click += (_, _) =>
            {
                _profile.MutedFindingIds.Clear();
                BlessedProfileStore.Save(_profile);
                RenderCurrentPage();
                _ = RunWatchAsync(auto: false);
            };
            card.Children.Add(button);
        }
        return card;
    }

    private StackPanel BuildSettingsShortcutCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("Twój profil i automatyzacje", 14, "TextPrimaryBrush", FontWeights.SemiBold));
        card.Children.Add(Text($"Priorytet: {_profile.PriorityLabel}. W ustawieniach zdecydujesz też, czy Blessed może sprzątać pliki tymczasowe automatycznie.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 10), lineHeight: 17));
        var button = new Button
        {
            Content = "Otwórz ustawienia Blessed",
            Style = (Style)FindResource("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 7, 12, 7)
        };
        button.Click += (_, _) => NavigateTo("settings");
        card.Children.Add(button);
        return card;
    }

    private Expander BuildSettingsDisclosure(string title, StackPanel content)
    {
        return new Expander
        {
            Header = Text(title, 10, "TextSecondaryBrush", FontWeights.SemiBold),
            Content = WrapPanel(content),
            IsExpanded = false,
            Margin = new Thickness(0, 3, 0, 3),
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.Transparent,
            Foreground = GetBrush("TextSecondaryBrush")
        };
    }

    private UIElement BuildSettingsPage()
    {
        var page = NewPage("USTAWIENIA BLESSED", "Dopasuj pomoc do siebie.", iconKey: "IconSettings", description:
            "Najpierw wybierz swój cel i sposób czuwania. Dodatkowe opcje są pogrupowane niżej.");
        page.Children.Add(WrapPanel(BuildPriorityCard()));
        page.Children.Add(WrapPanel(BuildAutomationCard()));
        page.Children.Add(BuildSettingsDisclosure("Wyciszone sprawy", BuildMutedCard()));

        var privacy = NewPanel();
        privacy.Children.Add(PanelHeading("IconShield", "Twoje dane zostają u Ciebie", "AccentBrush"));
        privacy.Children.Add(Text("Ustawienia profilu i historia przeglądów są przechowywane lokalnie. Sprawdzenie aktualizacji jest oddzielne.", 10, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0), lineHeight: 16));
        page.Children.Add(BuildSettingsDisclosure("Historia i prywatność", privacy));
        return page;
    }

    private UIElement BuildHistoryPage()
    {
        var page = NewPage("HISTORIA LOKALNA", "Zobacz, co zmieniało się z czasem.", iconKey: "IconClock", description:
            "Blessed przechowuje podsumowania ostatnich 30 dni wyłącznie na tym komputerze. Zapis obejmuje użycie CPU i RAM oraz liczbę znalezionych spraw.");
        var entries = WatchHistoryStore.ReadRecent(1500);
        var problemChecks = entries.Count(entry => entry.ProblemCount > 0);
        var averageMemory = entries.Count == 0 ? "—" : $"{entries.Average(entry => entry.MemoryPercent):0}%";
        var averageCpuSamples = entries.Where(entry => entry.CpuPercent.HasValue).Select(entry => entry.CpuPercent!.Value).ToArray();
        var averageCpu = averageCpuSamples.Length == 0 ? "—" : $"{averageCpuSamples.Average():0}%";

        var summary = new UniformGrid { Columns = 4, Rows = 1, Margin = new Thickness(0, 0, 0, 2) };
        summary.Children.Add(SpecCard("ZAPISANE PRZEGLĄDY", entries.Count.ToString(CultureInfo.CurrentCulture), "IconClock"));
        summary.Children.Add(SpecCard("Z PRZYPOMNIENIAMI", problemChecks.ToString(CultureInfo.CurrentCulture), "IconAlert"));
        summary.Children.Add(SpecCard("ŚREDNIE CPU", averageCpu, "IconCpu"));
        summary.Children.Add(SpecCard("ŚREDNIE UŻYCIE RAM", averageMemory, "IconMemory"));
        page.Children.Add(summary);

        var historyPanel = NewPanel();
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var clearButton = new Button
        {
            Content = "Wyczyść historię",
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(10, 6, 10, 6),
            IsEnabled = entries.Count > 0
        };
        clearButton.Click += (_, _) =>
        {
            var choice = MessageBox.Show(this,
                "Usunąć zapisaną historię przeglądów z tego komputera? Tej czynności nie można cofnąć.",
                "Wyczyścić historię?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (choice == MessageBoxResult.Yes)
            {
                FooterStatusText.Text = WatchHistoryStore.Clear() ? "Historia przeglądów została usunięta." : "Nie udało się usunąć historii.";
                RenderCurrentPage();
            }
        };
        DockPanel.SetDock(clearButton, Dock.Right);
        heading.Children.Add(clearButton);
        heading.Children.Add(Text("Ostatnie przeglądy", 15, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        historyPanel.Children.Add(heading);

        if (entries.Count == 0)
        {
            historyPanel.Children.Add(Text("Gdy Blessed wykona kilka przeglądów, znajdziesz je tutaj. Historia pozostaje na tym komputerze i możesz ją w każdej chwili wyczyścić.", 11, "TextSecondaryBrush", lineHeight: 18));
        }
        else
        {
            foreach (var entry in entries.Take(24))
            {
                var detail = entry.Findings.Count == 0 ? "Bez spraw wymagających uwagi" : string.Join(" · ", entry.Findings);
                var row = new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(11),
                    BorderThickness = new Thickness(1)
                };
                row.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
                row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                var content = new StackPanel();
                content.Children.Add(Text(entry.CompletedAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm", CultureInfo.CurrentCulture), 9, "AccentBrush", FontWeights.Bold));
                content.Children.Add(Text(entry.Headline, 12, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 4, 0, 0)));
                content.Children.Add(Text(detail, 10, "TextSecondaryBrush", margin: new Thickness(0, 3, 0, 0), lineHeight: 15));
                var cpu = entry.CpuPercent is { } cpuValue ? $"CPU {cpuValue:0}%" : "CPU —";
                content.Children.Add(Text($"{cpu} · RAM {entry.MemoryPercent:0}%", 9, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
                row.Child = content;
                historyPanel.Children.Add(row);
            }
            if (entries.Count > 24)
                historyPanel.Children.Add(Text("Pokazuję 24 najnowsze wpisy. Starsze podsumowania są nadal przechowywane do 30 dni.", 9, "TextSecondaryBrush", margin: new Thickness(0, 2, 0, 0)));
        }
        page.Children.Add(WrapPanel(historyPanel));
        return page;
    }

    private WrapPanel BuildPriorityChoices(bool completeOnboarding)
    {
        var choices = new WrapPanel();
        var options = new (BlessedPriority Priority, string Label)[]
        {
            (BlessedPriority.Gaming, "Granie i płynność"),
            (BlessedPriority.Work, "Praca i szybki start"),
            (BlessedPriority.Battery, "Długa bateria"),
            (BlessedPriority.Quiet, "Cisza i chłód")
        };
        foreach (var option in options)
        {
            var selected = option;
            var label = _profile.Priority == selected.Priority ? $"✓ {selected.Label}" : selected.Label;
            choices.Children.Add(ChoiceButton(label, () => SelectPriority(selected.Priority, completeOnboarding)));
        }
        return choices;
    }

    private UIElement AutomationSwitch(string label, bool value, Action<bool> onChanged, string helpText)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var help = HelpButton(helpText);
        DockPanel.SetDock(help, Dock.Right);
        row.Children.Add(help);
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0)
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.Checked += (_, _) => { onChanged(true); BlessedProfileStore.Save(_profile); };
        box.Unchecked += (_, _) => { onChanged(false); BlessedProfileStore.Save(_profile); };
        row.Children.Add(box);
        return row;
    }

    private void SelectPriority(BlessedPriority priority, bool completeOnboarding)
    {
        _profile.Priority = priority;
        if (completeOnboarding)
            _profile.OnboardingCompleted = true;
        BlessedProfileStore.Save(_profile);
        RenderCurrentPage();
        _ = RunWatchAsync(auto: false);
    }

    private void RenderFindings()
    {
        if (_careFindingsHost is null) return;
        _careFindingsHost.Children.Clear();
        if (_oneClickButton is not null)
            _oneClickButton.Visibility = _lastReport is { ProblemCount: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (_lastReport is null)
        {
            _careFindingsHost.Children.Add(Text("Robię pierwszy przegląd Twojego komputera…", 11, "TextSecondaryBrush"));
            return;
        }
        var index = 0;
        foreach (var finding in _lastReport.Findings)
        {
            var card = BuildFindingCard(finding);
            _careFindingsHost.Children.Add(card);
            AnimateIn(card, 12, index * 55, 260);
            index++;
        }
    }

    private Border BuildFindingCard(BlessedFinding finding)
    {
        var accent = finding.Severity switch
        {
            FindingSeverity.Critical => ("#E2685C", "ZAJMIJMY SIĘ TYM"),
            FindingSeverity.Warning => ("#E5BC67", "WARTO ZROBIĆ"),
            FindingSeverity.Info => ("#69B9F5", "DROBIAZG"),
            _ => ("#78D7B5", "W PORZĄDKU")
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(SeverityBadge(FindingIconKey(finding), accent.Item1));

        var copy = new StackPanel();
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        labelRow.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = BrushFrom(accent.Item1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        labelRow.Children.Add(Text(accent.Item2, 8, "TextSecondaryBrush", FontWeights.Bold));
        copy.Children.Add(labelRow);
        copy.Children.Add(Text(finding.Title, 14, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 5, 0, 0)));
        copy.Children.Add(Text(finding.Message, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0), lineHeight: 18));
        if (finding.Action != FindingAction.None && finding.ActionLabel is { } actionLabel)
        {
            var isAutomatic = finding.Action == FindingAction.BlessedHandlesIt;
            var button = new Button
            {
                Content = ButtonContent(
                    isAutomatic ? "IconBroom" : finding.Action == FindingAction.OpenPage ? "IconList" : "IconSpark",
                    actionLabel,
                    isAutomatic ? "AccentTextBrush" : "TextPrimaryBrush"),
                Style = (Style)FindResource(isAutomatic ? "PrimaryButton" : "SecondaryButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(13, 8, 13, 8)
            };
            button.Click += async (_, _) => await HandleFindingAsync(finding);
            copy.Children.Add(button);
        }
        Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);

        var card = new Border
        {
            Child = layout,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 9),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        AddHoverLift(card);

        if (finding.Severity != FindingSeverity.Good)
        {
            var mute = new Button
            {
                Content = "✕",
                ToolTip = "Nie pokazuj tego więcej",
                Style = (Style)FindResource("WindowControlButton"),
                Width = 26,
                Height = 26,
                FontSize = 11,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            mute.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            mute.MouseEnter += (_, _) => mute.Opacity = 1;
            mute.MouseLeave += (_, _) => mute.Opacity = 0.55;
            mute.Click += (_, _) => MuteFinding(finding, card);
            Grid.SetColumn(mute, 2);
            layout.Children.Add(mute);
        }
        return card;
    }

    private void MuteFinding(BlessedFinding finding, Border card)
    {
        if (_profile.MutedFindingIds.Contains(finding.Id))
            return;
        _profile.MutedFindingIds.Add(finding.Id);
        BlessedProfileStore.Save(_profile);
        _careFindingsHost?.Children.Remove(card);
        if (_lastReport is { } report)
        {
            var remaining = report.Findings.Where(item => item.Id != finding.Id).ToArray();
            _lastReport = new WatchReport(report.CompletedAt, remaining);
        }
        UpdateCareBadge();
        SetCareStatus("Wyciszyłem tę sprawę — przywrócisz ją w Ustawieniach, sekcja „Wyciszone sprawy”.");
    }

    private static string FindingIconKey(BlessedFinding finding) => finding.Id switch
    {
        "disk-space" => "IconDisk",
        "temp-files" => "IconBroom",
        "memory-pressure" => "IconMemory",
        "cpu-pressure" => "IconCpu",
        "uptime" => "IconClock",
        "startup-crowd" => "IconRestart",
        "display-hz" => "IconDisplay",
        "power-cpu-limit" or "power-battery" => "IconBolt",
        "battery-low" or "battery-full" => "IconBattery",
        "drive-health" => "IconHeart",
        "update-reboot-pending" or "update-stale" => "IconUpdate",
        "defender-realtime-off" or "defender-signature-stale" or "firewall-off" => "IconShield",
        "time-sync-off" => "IconClock",
        "hiberfile-size" => "IconMoon",
        "recycle-bin" => "IconTrash",
        "appx-crowd" => "IconBroom",
        "all-clear" => "IconCheck",
        _ => "IconSpark"
    };

    private async Task HandleFindingAsync(BlessedFinding finding)
    {
        switch (finding.Action)
        {
            case FindingAction.OpenSettings when finding.ActionTarget is { } uri:
                OpenWindowsSettings(uri);
                break;
            case FindingAction.OpenPage when finding.ActionTarget is { } target:
                NavigateTo(target);
                break;
            case FindingAction.BlessedHandlesIt when finding.ActionTarget == "temp-cleanup":
                await RunTempCleanupAsync(auto: false);
                await RunWatchAsync(auto: false);
                break;
            case FindingAction.BlessedHandlesIt when finding.ActionTarget == "recycle-bin-empty":
                await RunRecycleBinCleanupAsync();
                await RunWatchAsync(auto: false);
                break;
        }
    }

    private void NavigateTo(string page)
    {
        if (page != "gaming" && _monitoring)
            StopMonitoring();
        if (page != "processes")
            _processTimer.Stop();
        _currentPage = page;
        UpdateNavigationState();
        RenderCurrentPage();
    }

    private void CareTimer_Tick(object? sender, EventArgs e) => _ = RunWatchAsync(auto: true);

    private async Task RunWatchAsync(bool auto)
    {
        if (_careBusy) return;
        _careBusy = true;
        if (_careScanButton is not null) _careScanButton.IsEnabled = false;
        SetScanningIndicator(true);
        SetCareStatus("Sprawdzam Twój komputer…");
        try
        {
            _carePerformance.Read();
            await Task.Delay(TimeSpan.FromMilliseconds(700), _lifetime.Token);
            var usage = _carePerformance.Read();
            _lastReport = await _watchService.InspectAsync(_snapshot, _profile, usage, _lifetime.Token);

            if (_profile.AllowTempCleanup && ShouldAutoClean())
            {
                await RunTempCleanupAsync(auto: true);
                _lastReport = await _watchService.InspectAsync(_snapshot, _profile, usage, _lifetime.Token);
            }

            var completedReport = _lastReport;
            if (completedReport is not null)
                WatchHistoryStore.Record(completedReport, usage);

            RenderFindings();
            UpdateCareBadge();
            SetCareStatus(DescribeWatchStatus());
            if (completedReport is not null)
            {
                if (_currentPage == "care")
                    BlessedMessage.Text = completedReport.Headline;
                else if (completedReport.ProblemCount > 0)
                    BlessedMessage.Text = $"{completedReport.Headline} Zajrzyj do zakładki „Blessed czuwa” — mam gotowe rozwiązania.";
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            SetCareStatus($"Przegląd przerwany: {ex.Message}");
        }
        finally
        {
            _careBusy = false;
            SetScanningIndicator(false);
            if (_careScanButton is not null) _careScanButton.IsEnabled = true;
        }
    }

    private bool ShouldAutoClean()
    {
        if (_watchService.LastTempScan is not { } scan || scan.SizeMb < 1000)
            return false;
        if (DateTimeOffset.Now - _lastAutoCleanupAt < TimeSpan.FromHours(6))
            return false;
        if (_profile.LastCleanupAt is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(12))
            return false;
        _lastAutoCleanupAt = DateTimeOffset.Now;
        return true;
    }

    private async Task RunTempCleanupAsync(bool auto)
    {
        SetCareStatus(auto ? "Sprzątam pliki tymczasowe…" : "Sprawdzam, co można bezpiecznie posprzątać…");
        try
        {
            if (!auto)
            {
                var preview = await MaintenanceService.ScanTempAsync(TimeSpan.FromDays(2), _lifetime.Token);
                if (preview.FileCount == 0 || preview.SizeMb <= 0)
                {
                    MessageBox.Show(this,
                        "Nie znalazłem starych plików tymczasowych do usunięcia.",
                        "Blessed — sprzątanie",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    SetCareStatus("Nie ma starych plików do sprzątnięcia.");
                    return;
                }

                var choice = MessageBox.Show(this,
                    $"Podgląd sprzątania:\n\n• {preview.FileCount:N0} plików starszych niż 2 dni\n• około {preview.SizeMb:0} MB do usunięcia\n• pliki używane przez inne programy zostaną pominięte\n\nUsuniętych plików nie da się przywrócić. Kontynuować?",
                    "Zanim Blessed posprząta",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (choice != MessageBoxResult.Yes)
                {
                    SetCareStatus("Sprzątanie anulowano.");
                    return;
                }
            }

            var result = await MaintenanceService.CleanTempAsync(TimeSpan.FromDays(2), _lifetime.Token);
            _watchService.InvalidateTempScan();
            _profile.LastCleanupAt = DateTimeOffset.Now;
            _profile.TotalFreedMb += result.FreedMb;
            _profile.HandledCount++;
            BlessedProfileStore.Save(_profile);
            FooterStatusText.Text = $"Blessed zwolnił {result.FreedMb:0} MB · {result.DeletedFiles} plików";
            if (!auto)
            {
                MessageBox.Show(this,
                    $"Gotowe. Zwolniłem {result.FreedMb:0} MB w {result.DeletedFiles} plikach.\n\nPliki używane w tej chwili przez programy zostawiłem nietknięte: {result.SkippedFiles}.",
                    "Blessed posprzątał", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetCareStatus("Część plików była zajęta — dokończę sprzątanie przy następnym przeglądzie.");
        }
    }

    private async Task RunRecycleBinCleanupAsync()
    {
        SetCareStatus("Sprawdzam zawartość koszy…");
        RecycleBinScan scan;
        try
        {
            scan = await Task.Run(() => MaintenanceService.ScanRecycleBin(), _lifetime.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"Nie udało się odczytać koszy: {ex.Message}", "Blessed Optimizer — kosz", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetCareStatus("Nie udało się odczytać koszy.");
            return;
        }

        if (scan.FileCount == 0 || scan.SizeMb <= 0)
        {
            MessageBox.Show(this, "Kosze są już puste — nie ma czego opróżniać.", "Blessed — kosz", MessageBoxButton.OK, MessageBoxImage.Information);
            SetCareStatus("Kosze są już puste.");
            return;
        }

        var choice = MessageBox.Show(this,
            $"Podgląd opróżniania kosza:\n\n• {scan.FileCount:N0} plików we wszystkich koszach\n• około {scan.SizeMb / 1024:0.#} GB do odzyskania\n\nUWAGA: opróżnienie kosza trwale usuwa te pliki — tego kroku nie da się cofnąć.\n\nNa pewno opróżnić wszystkie kosze?",
            "Zanim Blessed opróżni kosz",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
        {
            SetCareStatus("Opróżnianie kosza anulowano.");
            return;
        }

        SetCareStatus("Opróżniam kosze…");
        var emptied = await Task.Run(() => MaintenanceService.EmptyRecycleBin(), _lifetime.Token);
        if (emptied)
        {
            _profile.TotalFreedMb += scan.SizeMb;
            _profile.HandledCount++;
            BlessedProfileStore.Save(_profile);
            FooterStatusText.Text = $"Blessed opróżnił kosze · odzyskano około {scan.SizeMb / 1024:0.#} GB";
            MessageBox.Show(this,
                $"Gotowe. Kosze opróżnione — odzyskałeś około {scan.SizeMb / 1024:0.#} GB.\n\nTo była trwała operacja: usuniętych plików nie da się przywrócić.",
                "Blessed posprzątał", MessageBoxButton.OK, MessageBoxImage.Information);
            SetCareStatus("Kosze opróżnione.");
        }
        else
        {
            MessageBox.Show(this,
                "Windows nie pozwolił opróżnić wszystkich koszy. Sprawdź, czy żaden program nie używa właśnie usuwanych plików, i spróbuj ponownie.",
                "Blessed Optimizer — kosz", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetCareStatus("Nie udało się opróżnić wszystkich koszy.");
        }
    }

    private async Task RunOneClickOptimizeAsync()
    {
        if (_careBusy || _lastReport is null)
            return;
        _careBusy = true;
        if (_careScanButton is not null) _careScanButton.IsEnabled = false;
        if (_oneClickButton is not null) _oneClickButton.IsEnabled = false;
        SetScanningIndicator(true);
        try
        {
            SetCareStatus("Jeszcze raz sprawdzam, co mogę zrobić jednym kliknięciem…");
            _carePerformance.Read();
            await Task.Delay(TimeSpan.FromMilliseconds(700), _lifetime.Token);
            var usage = _carePerformance.Read();
            _lastReport = await _watchService.InspectAsync(_snapshot, _profile, usage, _lifetime.Token);
            RenderFindings();
            if (_lastReport is not { } report || report.Findings.Count == 0)
            {
                SetCareStatus(DescribeWatchStatus());
                MessageBox.Show(this,
                    "Przeskanowałem komputer jeszcze raz i nie znalazłem nic do zrobienia — jest już ustawiony tak, jak trzeba.",
                    "Blessed Optimizer — wszystko gotowe", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var autoFindings = BlessedWatchService.AutoApplicableFindings(report);
            var wantsTemp = autoFindings.Any(finding => finding.ActionTarget == "temp-cleanup");
            var wantsRecycle = autoFindings.Any(finding => finding.ActionTarget == "recycle-bin-empty");
            var powerFinding = report.Findings.FirstOrDefault(finding => BlessedWatchService.IsOneClickPowerFixApplicable(finding, _profile));
            PowerPlanSnapshot? plan = null;
            PowerSettingState? processorState = null;
            if (powerFinding is not null)
            {
                try
                {
                    plan = PowerSettingsService.ReadActivePlan();
                    processorState = plan.Settings.FirstOrDefault(state => string.Equals(state.Descriptor.Key, "processor-max", StringComparison.Ordinal));
                    if (processorState is null || processorState.AcValue >= 100)
                    {
                        plan = null;
                        processorState = null;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    plan = null;
                    processorState = null;
                }
            }

            var manualFindings = report.Findings
                .Where(finding => finding.Action != FindingAction.None && !autoFindings.Contains(finding) && finding != powerFinding)
                .ToArray();

            TempScanResult? tempPreview = null;
            if (wantsTemp)
            {
                try
                {
                    tempPreview = await MaintenanceService.ScanTempAsync(TimeSpan.FromDays(2), _lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    tempPreview = null;
                }
            }
            RecycleBinScan? recyclePreview = null;
            if (wantsRecycle)
            {
                try
                {
                    recyclePreview = await Task.Run(() => MaintenanceService.ScanRecycleBin(), _lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    recyclePreview = null;
                }
            }

            var planText = new System.Text.StringBuilder();
            planText.Append("Plan „jednym kliknięciem” — Blessed zrobi za Ciebie:\n\n");
            var plannedAny = false;
            if (tempPreview is { FileCount: > 0 } && tempPreview.SizeMb > 0)
            {
                plannedAny = true;
                planText.Append($"• Posprzątam pliki tymczasowe: około {tempPreview.SizeMb:0} MB w {tempPreview.FileCount:N0} plikach starszych niż 2 dni\n");
            }
            if (recyclePreview is { FileCount: > 0 } && recyclePreview.SizeMb > 0)
            {
                plannedAny = true;
                planText.Append($"• Opróżnię wszystkie kosze: około {recyclePreview.SizeMb / 1024:0.#} GB — UWAGA: usunięcie z kosza jest trwałe i nieodwracalne\n");
            }
            if (processorState is not null && plan is not null)
            {
                plannedAny = true;
                planText.Append($"• Podniosę limit procesora przy zasilaniu z sieci do 100% w planie „{plan.SchemeName}” (na baterii bez zmian; oryginalne wartości zapiszę do przywrócenia)\n• Dla tej zmiany Windows poprosi o zgodę administratora (UAC) — możesz odmówić\n");
            }
            if (!plannedAny && manualFindings.Length == 0)
            {
                SetCareStatus(DescribeWatchStatus());
                MessageBox.Show(this,
                    "Przeskanowałem komputer jeszcze raz — wszystko, co mogę bezpiecznie zrobić, jest już zrobione.",
                    "Blessed Optimizer — wszystko gotowe", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (manualFindings.Length > 0)
            {
                planText.Append($"\nResztę ({manualFindings.Length}) załatwisz osobnymi przyciskami na liście poniżej:\n");
                foreach (var finding in manualFindings)
                    planText.Append($"• {finding.Title}\n");
            }
            planText.Append("\nKontynuować?");
            var choice = MessageBox.Show(this, planText.ToString(), "Blessed Optimizer — zrób wszystko za mnie", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes)
            {
                SetCareStatus("Anulowałeś — nic nie zmieniłem.");
                return;
            }

            var done = new List<string>();
            var skipped = new List<string>();
            if (tempPreview is { FileCount: > 0 } && tempPreview.SizeMb > 0)
            {
                SetCareStatus("Sprzątam pliki tymczasowe…");
                try
                {
                    var clean = await MaintenanceService.CleanTempAsync(TimeSpan.FromDays(2), _lifetime.Token);
                    _watchService.InvalidateTempScan();
                    _profile.LastCleanupAt = DateTimeOffset.Now;
                    _profile.TotalFreedMb += clean.FreedMb;
                    _profile.HandledCount++;
                    done.Add($"posprzątałem {clean.FreedMb:0} MB w {clean.DeletedFiles} plikach tymczasowych");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    skipped.Add($"część plików tymczasowych była zajęta ({ex.Message}) — dokończę przy następnym przeglądzie");
                }
            }
            if (recyclePreview is { FileCount: > 0 } && recyclePreview.SizeMb > 0)
            {
                SetCareStatus("Opróżniam kosze…");
                var emptied = await Task.Run(() => MaintenanceService.EmptyRecycleBin(), _lifetime.Token);
                if (emptied)
                {
                    _profile.TotalFreedMb += recyclePreview.SizeMb;
                    _profile.HandledCount++;
                    done.Add($"opróżniłem kosze — odzyskałeś około {recyclePreview.SizeMb / 1024:0.#} GB");
                }
                else
                {
                    skipped.Add("Windows nie pozwolił opróżnić wszystkich koszy");
                }
            }
            if (processorState is not null && plan is not null)
            {
                SetCareStatus("Ustawiam procesor na 100% przy zasilaniu z sieci…");
                try
                {
                    PowerSettingsService.SaveOriginalIfNeeded(processorState, plan.SchemeId);
                    var applied = await RunElevatedPowerHelperAsync(plan.SchemeId, processorState.Descriptor.Key, 100, processorState.DcValue);
                    if (applied)
                    {
                        _profile.HandledCount++;
                        done.Add($"procesor w planie „{plan.SchemeName}” pracuje na 100% przy zasilaniu z sieci (oryginał zapisany)");
                    }
                    else
                    {
                        skipped.Add("zmiana planu zasilania została anulowana lub odrzucona przez Windows (UAC)");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or System.Text.Json.JsonException)
                {
                    skipped.Add($"nie udało się zmienić planu zasilania: {ex.Message}");
                }
            }
            BlessedProfileStore.Save(_profile);

            var summary = new System.Text.StringBuilder();
            summary.Append(done.Count > 0 ? "Gotowe — oto co zrobiłem:\n\n" : "Nie zmieniłem nic — oto dlaczego:\n\n");
            foreach (var line in done)
                summary.Append($"✓ {line}\n");
            foreach (var line in skipped)
                summary.Append($"• {line}\n");
            if (manualFindings.Length > 0)
                summary.Append($"\nNa liście poniżej czeka jeszcze {manualFindings.Length} spraw do załatwienia osobnymi przyciskami.");
            MessageBox.Show(this, summary.ToString(), "Blessed Optimizer — jednym kliknięciem", MessageBoxButton.OK, MessageBoxImage.Information);
            FooterStatusText.Text = done.Count > 0
                ? $"Jednym kliknięciem: {string.Join(" · ", done)}"
                : "Jednym kliknięciem nic nie zmieniłem.";
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex)
        {
            SetCareStatus($"Optymalizacja przerwana: {ex.Message}");
        }
        finally
        {
            _careBusy = false;
            SetScanningIndicator(false);
            if (_careScanButton is not null) _careScanButton.IsEnabled = true;
            if (_oneClickButton is not null) _oneClickButton.IsEnabled = true;
        }
        await RunWatchAsync(auto: false);
    }

    private void SetCareStatus(string message)
    {
        if (_careStatusText is not null)
            _careStatusText.Text = message;
    }

    private string DescribeWatchStatus()
    {
        if (_lastReport is null)
            return "Przygotowuję pierwszy przegląd…";
        return $"{_lastReport.Headline} · przegląd o {_lastReport.CompletedAt.ToString("HH:mm", CultureInfo.CurrentCulture)}";
    }

    private void UpdateCareBadge()
    {
        var count = _lastReport?.ProblemCount ?? 0;
        var hadBadge = CareBadge.Visibility == Visibility.Visible;
        CareBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CareBadgeText.Text = count.ToString(CultureInfo.InvariantCulture);
        if (count > 0 && !hadBadge)
            PopCareBadge();
        WorkspaceStatusText.Text = count == 0 ? "Blessed czuwa · czysto" : $"Blessed znalazł {count}";
    }

    private static StackPanel NewPage(string kicker, string title, string description, string? iconKey = null)
    {
        var page = new StackPanel();
        var heading = new StackPanel();
        heading.Children.Add(Text(kicker, 9, "AccentBrush", FontWeights.Bold));
        heading.Children.Add(Text(title, 19, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        heading.Children.Add(Text(description, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0), lineHeight: 18));

        if (iconKey is null)
        {
            heading.Margin = new Thickness(0, 2, 0, 14);
            page.Children.Add(heading);
            return page;
        }

        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 14) };
        var badge = IconBadge(iconKey, "GoldBrush", 44);
        badge.Margin = new Thickness(0, 2, 14, 0);
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(heading);
        page.Children.Add(row);
        return page;
    }

    // ----- Ikony i animacje -----

    private static System.Windows.Shapes.Path IconPath(string geometryKey, double size = 16, string brushKey = "AccentBrush", Thickness? margin = null)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)Application.Current.FindResource(geometryKey),
            Style = (Style)Application.Current.FindResource("BlessedIcon"),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? new Thickness(0)
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
        return icon;
    }

    private static Border IconBadge(string geometryKey, string brushKey, double size = 34)
    {
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 3),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = IconPath(geometryKey, Math.Round(size * 0.52), brushKey)
        };
        badge.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return badge;
    }

    private static Border SeverityBadge(string geometryKey, string hex, double size = 36)
    {
        var color = ColorFrom(hex);
        var icon = IconPath(geometryKey, Math.Round(size * 0.52));
        icon.Stroke = new SolidColorBrush(color);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 3),
            Background = new SolidColorBrush(Color.FromArgb(42, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(96, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 13, 0),
            Child = icon
        };
    }

    private DockPanel TextWithHelp(string label, string helpText, double fontSize = 11, string brushKey = "TextSecondaryBrush", FontWeight? weight = null, Thickness? margin = null)
    {
        var row = new DockPanel { LastChildFill = true, Margin = margin ?? new Thickness(0) };
        var help = HelpButton(helpText);
        DockPanel.SetDock(help, Dock.Right);
        row.Children.Add(help);
        row.Children.Add(Text(label, fontSize, brushKey, weight));
        return row;
    }

    private DockPanel PanelHeadingWithHelp(string iconKey, string title, string helpText, string brushKey = "GoldBrush", double fontSize = 13, Thickness? margin = null)
    {
        var row = new DockPanel { LastChildFill = true, Margin = margin ?? new Thickness(0) };
        var help = HelpButton(helpText);
        DockPanel.SetDock(help, Dock.Right);
        row.Children.Add(help);
        row.Children.Add(PanelHeading(iconKey, title, brushKey, fontSize));
        return row;
    }

    private Button HelpButton(string helpText)
    {
        var button = new Button
        {
            Content = "?",
            Style = (Style)FindResource("HelpIconButton"),
            ToolTip = helpText,
            Focusable = true
        };
        button.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Wyjaśnij tę opcję");
        button.Click += (_, _) => MessageBox.Show(this, helpText, "Pomoc Blessed", MessageBoxButton.OK, MessageBoxImage.Information);
        return button;
    }

    private StackPanel HelpHeader(string label, string helpText)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(Text(label, 9, "TextSecondaryBrush", FontWeights.SemiBold));
        var help = HelpButton(helpText);
        help.Width = 18;
        help.Height = 18;
        help.MinWidth = 18;
        help.Margin = new Thickness(4, 0, 0, 0);
        header.Children.Add(help);
        return header;
    }

    private static DockPanel PanelHeading(string iconKey, string title, string brushKey = "GoldBrush", double fontSize = 13, Thickness? margin = null)
    {
        var row = new DockPanel { Margin = margin ?? new Thickness(0) };
        var icon = IconPath(iconKey, fontSize + 3, brushKey, new Thickness(0, 0, 9, 0));
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Text(title, fontSize, brushKey, FontWeights.SemiBold));
        return row;
    }

    private static StackPanel ButtonContent(string geometryKey, string label, string brushKey)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(IconPath(geometryKey, 14, brushKey, new Thickness(0, 0, 8, 0)));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private static void AnimateIn(FrameworkElement element, double offset = 14, int delayMs = 0, int durationMs = 290)
    {
        var slide = new TranslateTransform(0, offset);
        if (element.RenderTransform is Transform existing && existing != Transform.Identity)
        {
            var group = new TransformGroup();
            group.Children.Add(existing);
            group.Children.Add(slide);
            element.RenderTransform = group;
        }
        else
        {
            element.RenderTransform = slide;
        }

        element.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = begin,
            EasingFunction = ease
        });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(durationMs + 70))
        {
            BeginTime = begin,
            EasingFunction = ease
        });
    }

    private static void AnimateBar(ProgressBar? bar, double value)
    {
        if (bar is null) return;
        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(Math.Clamp(value, 0, 100), TimeSpan.FromMilliseconds(430))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void AddHoverLift(FrameworkElement element)
    {
        var lift = new TranslateTransform();
        element.RenderTransform = lift;
        element.MouseEnter += (_, _) => lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-3, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        element.MouseLeave += (_, _) => lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void SetScanningIndicator(bool active)
    {
        if (active)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var opacity = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(720))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease
            };
            var scale = new DoubleAnimation(1, 1.12, TimeSpan.FromMilliseconds(720))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease
            };
            GuideLogo.BeginAnimation(OpacityProperty, opacity);
            _guideLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            _guideLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }
        else
        {
            GuideLogo.BeginAnimation(OpacityProperty, null);
            _guideLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _guideLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            GuideLogo.Opacity = 1;
            _guideLogoScale.ScaleX = 1;
            _guideLogoScale.ScaleY = 1;
        }
    }

    private void PopCareBadge()
    {
        var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }
        };
        _careBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _careBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private static StackPanel NewPanel()
    {
        var panel = new StackPanel();
        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(17),
            Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0, 14, 36),
                BlurRadius = 20,
                ShadowDepth = 5,
                Opacity = 0.22
            }
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        panel.Tag = border;
        return panel;
    }

    private static UIElement WrapPanel(StackPanel panel) => panel.Tag as UIElement ?? panel;

    private static TextBlock Text(string value, double size, string brushKey, FontWeight? weight = null, Thickness? margin = null, double? lineHeight = null)
    {
        var text = new TextBlock
        {
            Text = value,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0)
        };
        if (lineHeight is { } height) text.LineHeight = height;
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    private static Border SpecCard(string title, string value, string? iconKey = null)
    {
        var content = new StackPanel();
        content.Children.Add(Text(title, 9, "AccentBrush", FontWeights.Bold));
        content.Children.Add(Text(value, 11, "TextPrimaryBrush", FontWeights.Medium, new Thickness(0, 5, 0, 0)));
        UIElement body = content;
        if (iconKey is not null)
        {
            var row = new DockPanel();
            var icon = IconPath(iconKey, 17, "AccentBrush", new Thickness(0, 1, 11, 0));
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(content);
            body = row;
        }
        var border = new Border { Child = body, Padding = new Thickness(12), Margin = new Thickness(0, 0, 9, 9), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private Border BuildUsageMeter(string title, out TextBlock value, out ProgressBar bar)
    {
        var content = new StackPanel();
        content.Children.Add(Text(title, 9, "TextSecondaryBrush", FontWeights.Bold));
        value = Text("—", 21, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 8, 0, 5));
        content.Children.Add(value);
        bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7, Background = new SolidColorBrush(Color.FromRgb(37, 58, 82)), Foreground = GetBrush("AccentBrush"), BorderThickness = new Thickness(0) };
        bar.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        content.Children.Add(bar);
        var border = new Border { Child = content, Padding = new Thickness(13), Margin = new Thickness(0, 0, 9, 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.FindResource(key);

    private static Border ProposalCard(string title, string body, string buttonText, string? settingsUri)
    {
        var content = new StackPanel();
        content.Children.Add(Text(title, 15, "TextPrimaryBrush", FontWeights.SemiBold));
        content.Children.Add(Text(body, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 11), lineHeight: 18));
        if (settingsUri is not null)
        {
            var button = new Button { Content = buttonText, Tag = settingsUri, Style = (Style)Application.Current.FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 7, 12, 7) };
            button.Click += OpenSettingsButton_Click;
            content.Children.Add(button);
        }
        else
        {
            content.Children.Add(Text(buttonText, 10, "GoldBrush", FontWeights.SemiBold));
        }
        var card = new Border { Child = content, Padding = new Thickness(17), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private static Button ChoiceButton(string label, Action action)
    {
        var button = new Button { Content = label, Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(13, 8, 13, 8) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Border PreviewPanel()
    {
        var preview = new StackPanel();
        preview.Children.Add(Text("PODGLĄD MOTYWU", 9, "AccentBrush", FontWeights.Bold));
        preview.Children.Add(Text("Twój komputer, Twój klimat.", 20, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 9, 0, 0)));
        preview.Children.Add(Text("Kolorowe przyciski powyżej zmieniają tylko tę aplikację.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        var action = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 13, 0, 0), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left };
        action.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        action.Child = Text("Akcent Blessed", 11, "AccentTextBrush", FontWeights.SemiBold);
        preview.Children.Add(action);
        var border = new Border { Child = preview, Padding = new Thickness(18), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private void ToggleMonitoring_Click(object sender, RoutedEventArgs e)
    {
        _monitoring = !_monitoring;
        if (_monitoring)
        {
            _performanceMonitor.Read();
            _watchTimer.Start();
            _watchButton!.Content = "Zatrzymaj czuwanie";
            _monitoringStatus!.Text = "Czuwanie aktywne · lokalny pomiar co 2 sekundy";
            FooterStatusText.Text = "Czuwanie działa lokalnie · odczyt co 2 sekundy";
            UpdateLiveUsage();
        }
        else
        {
            StopMonitoring();
        }
    }

    private void StopMonitoring()
    {
        _monitoring = false;
        _watchTimer.Stop();
        if (_watchButton is not null) _watchButton.Content = "Włącz czuwanie";
        if (_monitoringStatus is not null) _monitoringStatus.Text = "Czuwanie wyłączone";
        FooterStatusText.Text = "Czuwanie zatrzymane · możesz je włączyć w każdej chwili";
        if (_cpuValue is not null) _cpuValue.Text = "—";
        if (_memoryValue is not null) _memoryValue.Text = "—";
        AnimateBar(_cpuBar, 0);
        AnimateBar(_memoryBar, 0);
    }

    private void WatchTimer_Tick(object? sender, EventArgs e) => UpdateLiveUsage();

    private void UpdateLiveUsage()
    {
        if (!_monitoring || _cpuValue is null || _memoryValue is null || _cpuBar is null || _memoryBar is null) return;
        var usage = _performanceMonitor.Read();
        _cpuValue.Text = usage.CpuPercent is { } cpu ? $"{cpu:0}%" : "Próbkuję…";
        _memoryValue.Text = usage.MemoryTotalGb > 0
            ? $"{usage.MemoryUsedGb:0.#} / {usage.MemoryTotalGb:0.#} GB · {usage.MemoryPercent:0}%"
            : "Brak odczytu";
        AnimateBar(_cpuBar, usage.CpuPercent ?? 0);
        AnimateBar(_memoryBar, usage.MemoryPercent);
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (_pingButton is null || _pingResult is null) return;
        _pingButton.IsEnabled = false;
        _pingResult.Text = "Wykonuję 10 krótkich prób połączenia…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            _pingResult.Text = await NetworkDiagnostics.TestConnectivityAsync(timeout.Token);
        }
        finally
        {
            if (_pingButton.IsLoaded)
            {
                _pingButton.IsEnabled = true;
            }
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(showUpToDateMessage: true);

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Sprawdzam wydania GitHub…";
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0);
            var result = await UpdateService.CheckLatestAsync(current, _lifetime.Token);
            UpdateStatusText.Text = result.Message;
            if (result.HasUpdate)
            {
                var choice = MessageBox.Show(this,
                    $"Jest dostępna aktualizacja Blessed Optimizer {result.LatestVersion}.\n\nPobrać instalator z GitHub Releases, sprawdzić jego sumę SHA-256 i zaktualizować kopię w Twoim profilu? Program uruchomi się ponownie, a ustawienia Windows pozostaną bez zmian.",
                    "Aktualizacja Blessed Optimizer",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (choice == MessageBoxResult.Yes)
                {
                    if (_isPortable)
                    {
                        Process.Start(new ProcessStartInfo("https://github.com/rejson59/Blessed-Optimizer/releases/latest") { UseShellExecute = true });
                        return;
                    }
                    await DownloadAndApplyUpdateAsync(result);
                }
            }
            else if (showUpToDateMessage)
            {
                MessageBox.Show(this, result.Message, "Blessed Optimizer — aktualizacje", MessageBoxButton.OK,
                    result.Message.StartsWith("Nie udało", StringComparison.Ordinal) ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "Sprawdzanie anulowano.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Aktualizacja nie została zainstalowana.";
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — aktualizacja", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _checkingUpdates = false;
            if (CheckUpdateButton.IsLoaded) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndApplyUpdateAsync(UpdateCheckResult update)
    {
        UpdateStatusText.Text = "Pobieram i weryfikuję aktualizację…";
        var progress = new Progress<double>(percent => UpdateStatusText.Text = $"Pobieram aktualizację… {percent:0}%");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var (installerPath, sha256) = await UpdateService.DownloadAndVerifyAsync(update, progress, timeout.Token);
        UpdateStatusText.Text = "SHA-256 zgodne · zamykam aplikację…";
        try
        {
            UpdateService.StartReplacementHelper(installerPath, sha256);
        }
        catch
        {
            try { File.Delete(installerPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        await Task.Delay(150);
        Application.Current.Shutdown();
    }

    private static void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string uri }) OpenWindowsSettings(uri);
    }

    private static void OpenWindowsSettings(string settingsUri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(settingsUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            MessageBox.Show($"Nie udało się otworzyć strony Ustawień Windows.\n\n{ex.Message}", "Blessed Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static void ApplyTheme(string? mode, string? accent)
    {
        var resources = Application.Current.Resources;
        if (mode == "light")
        {
            resources["WindowBackgroundBrush"] = BrushFrom("#EDF4FB");
            resources["TitleBarBrush"] = BrushFrom("#E5EEF7");
            resources["SurfaceBrush"] = BrushFrom("#FFFFFF");
            resources["WorkspaceBrush"] = BrushFrom("#F8FBFE");
            resources["GuideBrush"] = BrushFrom("#E7F2FC");
            resources["SurfaceRaisedBrush"] = BrushFrom("#E4EEF8");
            resources["StatusBrush"] = BrushFrom("#DDF3E9");
            resources["StatusTextBrush"] = BrushFrom("#275A46");
            resources["BorderBrush"] = BrushFrom("#C7D8E8");
            resources["TextPrimaryBrush"] = BrushFrom("#142A40");
            resources["TextSecondaryBrush"] = BrushFrom("#526B83");
        }
        else if (mode == "dark")
        {
            resources["WindowBackgroundBrush"] = BrushFrom("#07172C");
            resources["TitleBarBrush"] = BrushFrom("#082647");
            resources["SurfaceBrush"] = BrushFrom("#10243D");
            resources["WorkspaceBrush"] = BrushFrom("#0C233D");
            resources["GuideBrush"] = BrushFrom("#183653");
            resources["SurfaceRaisedBrush"] = BrushFrom("#172F4E");
            resources["StatusBrush"] = BrushFrom("#173C3D");
            resources["StatusTextBrush"] = BrushFrom("#C2F4DC");
            resources["BorderBrush"] = BrushFrom("#28435F");
            resources["TextPrimaryBrush"] = BrushFrom("#F2F7FC");
            resources["TextSecondaryBrush"] = BrushFrom("#A9BED2");
        }

        if (accent is not null)
        {
            var values = accent switch
            {
                "gold" => (Main: "#E5BC67", Hover: "#F2D68F", Text: "#2B210D"),
                "violet" => (Main: "#A99AF3", Hover: "#C0B5FF", Text: "#21194B"),
                "mint" => (Main: "#78D7B5", Hover: "#9AE8CD", Text: "#092D24"),
                _ => (Main: "#69B9F5", Hover: "#88CCFF", Text: "#08213B")
            };
            resources["AccentBrush"] = BrushFrom(values.Main);
            resources["AccentHoverBrush"] = BrushFrom(values.Hover);
            resources["AccentTextBrush"] = BrushFrom(values.Text);
        }
    }

    private static SolidColorBrush BrushFrom(string hex) => new(ColorFrom(hex));

    private static Color ColorFrom(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6) throw new FormatException("Nieprawidłowy kolor motywu.");
        var red = byte.Parse(value[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var green = byte.Parse(value[2..4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var blue = byte.Parse(value[4..6], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return Color.FromRgb(red, green, blue);
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 1, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleWindowState_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowMaximizeGlyph.Text = maximized ? "❐" : "□";
        WindowMaximizeButton.ToolTip = maximized ? "Przywróć" : "Maksymalizuj";
        WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(6);
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
        UpdateCursorWings();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        HideCursorWings();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        _watchTimer.Stop();
        _processTimer.Stop();
        _careTimer.Stop();
        _lifetime.Cancel();
    }
}
