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
    private DataGrid? _processGrid;
    private TextBox? _processSearch;
    private TextBlock? _processStatus;
    private IReadOnlyList<ProcessUsageSnapshot> _processRows = Array.Empty<ProcessUsageSnapshot>();
    private bool _processRefreshBusy;
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
            nameof(CareNavButton) => "care",
            nameof(ProcessesNavButton) => "processes",
            nameof(StartupNavButton) => "startup",
            nameof(PowerNavButton) => "power",
            nameof(ConnectionsNavButton) => "connections",
            nameof(ProposalsNavButton) => "proposals",
            nameof(PersonalizationNavButton) => "personalization",
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

    private void UpdateNavigationState()
    {
        CareNavButton.Tag = _currentPage == "care" ? "active" : null;
        GamingNavButton.Tag = _currentPage == "gaming" ? "active" : null;
        ProcessesNavButton.Tag = _currentPage == "processes" ? "active" : null;
        StartupNavButton.Tag = _currentPage == "startup" ? "active" : null;
        PowerNavButton.Tag = _currentPage == "power" ? "active" : null;
        ConnectionsNavButton.Tag = _currentPage == "connections" ? "active" : null;
        ProposalsNavButton.Tag = _currentPage == "proposals" ? "active" : null;
        PersonalizationNavButton.Tag = _currentPage == "personalization" ? "active" : null;
    }

    private void RenderCurrentPage()
    {
        UpdateWorkspaceHeader();
        PageHost.Children.Clear();
        var page = _currentPage switch
        {
            "gaming" => BuildGamingPage(),
            "processes" => BuildProcessesPage(),
            "startup" => BuildStartupPage(),
            "power" => BuildPowerPage(),
            "connections" => BuildConnectionsPage(),
            "proposals" => BuildProposalsPage(),
            "personalization" => BuildPersonalizationPage(),
            _ => BuildCarePage()
        };
        PageHost.Children.Add(page);
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

    private void UpdateWorkspaceHeader()
    {
        var page = _currentPage switch
        {
            "gaming" => (Title: "Strefa gracza", Crumb: "STREFA GRACZA", Message: "Włącz czuwanie, aby obserwować użycie procesora i pamięci podczas gry."),
            "connections" => (Title: "Połączenia", Crumb: "POŁĄCZENIA", Message: "Sprawdzę stan kart sieciowych i wykonam test ping. Każdą zmianę zatwierdzasz Ty."),
            "proposals" => (Title: "Propozycje", Crumb: "PROPOZYCJE", Message: "Podpowiem, co warto sprawdzić, i zaprowadzę Cię prosto do właściwych ustawień Windows."),
            "personalization" => (Title: "Personalizacja Windows", Crumb: "PERSONALIZACJA WINDOWS", Message: "Dopasuj wygląd Blessed do siebie — motyw i akcent zmieniają się od razu."),
            "processes" => (Title: "Procesy", Crumb: "PROCESY", Message: "Pokażę zużycie CPU i pamięci przez każdy proces — czytelnie i na żywo."),
            "startup" => (Title: "Autostart", Crumb: "AUTOSTART", Message: "Przejrzyj wpisy autostartu swojego konta. Każda zmiana ma zapisaną kopię do przywrócenia."),
            "power" => (Title: "Zasilanie", Crumb: "ZASILANIE", Message: "Dostrój plan zasilania. Każdą zmianę potwierdzasz Ty i zawsze możesz ją cofnąć."),
            _ => (Title: "Blessed czuwa", Crumb: "BLESSED CZUWA", Message: _lastReport is null
                ? "Robię przegląd Twojego komputera i zaraz powiem, czym się zająć."
                : _lastReport.Headline)
        };

        WorkspaceKicker.Text = _isPortable ? "TRYB PRZENOŚNY · DANE LOKALNE" : "TWÓJ PANEL · DANE NA ŻYWO";
        WorkspaceTitle.Text = page.Title;
        CrumbTitle.Text = page.Crumb;
        BlessedMessage.Text = page.Message;
        WorkspaceStatusText.Text = _snapshot is null ? "Odczytuję komputer" : "Blessed jest gotowy";
    }

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
            "Podgląd CPU, pamięci i czasu uruchomienia każdego procesu. Lista odświeża się co 2 sekundy, gdy ta karta jest otwarta.");
        var panel = NewPanel();
        var toolbar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        _processSearch = new TextBox
        {
            Width = 260,
            Height = 36,
            Padding = new Thickness(10, 6, 10, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderBrush"),
            ToolTip = "Filtruj po nazwie procesu lub PID"
        };
        _processSearch.TextChanged += (_, _) => FilterProcessGrid();
        toolbar.Children.Add(_processSearch);
        var refresh = new Button { Content = "Odśwież teraz", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(11, 7, 11, 7) };
        refresh.Click += async (_, _) => await RefreshProcessesAsync();
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(refresh);
        _processStatus = Text("Przygotowuję pierwszy pomiar…", 10, "TextSecondaryBrush", margin: new Thickness(0, 9, 0, 0));
        DockPanel.SetDock(_processStatus, Dock.Bottom);
        toolbar.Children.Add(_processStatus);
        panel.Children.Add(toolbar);

        _processGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            CanUserSortColumns = true,
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
            Margin = new Thickness(0, 0, 0, 5)
        };
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, GetBrush("SurfaceRaisedBrush")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, GetBrush("TextSecondaryBrush")));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 7, 8, 7)));
        _processGrid.ColumnHeaderStyle = headerStyle;
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "Proces", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 130 });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "PID", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.ProcessId)), Width = 72 });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "CPU", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.CpuPercent)) { StringFormat = "{0:0.0}%" }, Width = 80 });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "RAM", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.WorkingSetMb)) { StringFormat = "{0:N0} MB" }, Width = 100 });
        _processGrid.Columns.Add(new DataGridTextColumn { Header = "Uruchomiono", Binding = new System.Windows.Data.Binding(nameof(ProcessUsageSnapshot.StartedAt)) { StringFormat = "{0:g}" }, Width = 130 });
        panel.Children.Add(_processGrid);
        page.Children.Add(WrapPanel(panel));

        var safety = NewPanel();
        safety.Children.Add(PanelHeading("IconCpu", "Pełny obraz zasobów"));
        safety.Children.Add(Text("Widzisz, co realnie zajmuje procesor i pamięć — bez ingerencji w działające programy i zabezpieczenia Windows.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(safety));
        return page;
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
            _processRows = rows;
            FilterProcessGrid();
            if (_processStatus is not null)
                _processStatus.Text = $"{rows.Count} procesów · odczyt lokalny {DateTime.Now:HH:mm:ss} · CPU liczone z próbek co 2 s";
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
        var query = _processSearch?.Text.Trim() ?? string.Empty;
        IEnumerable<ProcessUsageSnapshot> visibleRows = string.IsNullOrWhiteSpace(query)
            ? _processRows
            : _processRows.Where(row => row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                        row.ProcessId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal)).ToArray();
        _processGrid.ItemsSource = visibleRows;
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
        note.Children.Add(PanelHeading("IconRestart", "Cofnięcie zmian", fontSize: 12));
        note.Children.Add(Text("Pierwsza zmiana danej opcji zapisuje oryginalne wartości AC i baterii w profilu użytkownika. Przycisk „Przywróć oryginał” odtwarza te wartości; samo przełączanie profilu nie kasuje kopii.", 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(note));
        return page;
    }

    private UIElement BuildPowerSettingCard(PowerPlanSnapshot snapshot, PowerSettingState state)
    {
        var panel = NewPanel();
        panel.Children.Add(Text(state.Descriptor.Name, 14, "TextPrimaryBrush", FontWeights.SemiBold));
        panel.Children.Add(Text(state.Descriptor.Description, 10, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 11), lineHeight: 16));

        var controls = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.Children.Add(Text("Zasilanie z sieci", 10, "TextSecondaryBrush", FontWeights.SemiBold, new Thickness(0, 0, 8, 4)));
        var batteryLabel = Text("Bateria / UPS", 10, "TextSecondaryBrush", FontWeights.SemiBold, new Thickness(8, 0, 0, 4));
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
        testCard.Children.Add(Text("Jednorazowy test łączności", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        testCard.Children.Add(Text("Test mierzy czas odpowiedzi serwera 1.1.1.1 (ICMP). Ustawienia Wi-Fi, DNS i TCP pozostają bez zmian.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 12)));
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
            $"Sam sprawdzam dysk, pamięć, autostart, ekran, baterię i plan zasilania — co kilka minut, w tle. {_profile.PriorityPromise}");

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
        status.Children.Add(statusBar);
        status.Children.Add(BuildCareSummaryRow());
        page.Children.Add(WrapPanel(status));

        var findings = NewPanel();
        findings.Children.Add(Text("CO ZNALAZŁEM", 9, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 11)));
        _careFindingsHost = new StackPanel();
        findings.Children.Add(_careFindingsHost);
        page.Children.Add(WrapPanel(findings));
        RenderFindings();

        page.Children.Add(WrapPanel(BuildPriorityCard()));
        return page;
    }

    private UIElement BuildCareSummaryRow()
    {
        var grid = new UniformGrid { Columns = 3, Rows = 1 };
        grid.Children.Add(SpecCard("OSTATNI PRZEGLĄD", _lastReport is null
            ? "za chwilę"
            : _lastReport.CompletedAt.ToString("HH:mm", CultureInfo.CurrentCulture), "IconClock"));
        grid.Children.Add(SpecCard("ZWOLNIŁEM DLA CIEBIE", _profile.TotalFreedMb >= 1 ? $"{_profile.TotalFreedMb:0} MB" : "jeszcze nic", "IconBroom"));
        grid.Children.Add(SpecCard("CZUWANIE", _profile.WatchInBackground ? "W tle, co 3 minuty" : "Tylko na żądanie", "IconShield"));
        return grid;
    }

    private StackPanel BuildOnboardingCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("ZANIM ZACZNIEMY", 9, "GoldBrush", FontWeights.Bold));
        card.Children.Add(Text("Powiedz mi, co jest dla Ciebie najważniejsze.", 17, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        card.Children.Add(Text("Pod to dobiorę przegląd, kolejność spraw i to, czym zajmę się sam.", 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 13), lineHeight: 18));
        card.Children.Add(BuildPriorityChoices(completeOnboarding: true));
        return card;
    }

    private StackPanel BuildPriorityCard()
    {
        var card = NewPanel();
        card.Children.Add(Text("CO JEST DLA CIEBIE WAŻNE", 9, "AccentBrush", FontWeights.Bold));
        card.Children.Add(Text($"Twój priorytet: {_profile.PriorityLabel}", 15, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
        card.Children.Add(Text(_profile.PriorityPromise, 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 12), lineHeight: 18));
        card.Children.Add(BuildPriorityChoices(completeOnboarding: false));

        card.Children.Add(PanelHeading("IconCheck", "Czym mogę zająć się sam?", "TextPrimaryBrush", 13, new Thickness(0, 17, 0, 9)));
        card.Children.Add(AutomationSwitch("Czuwam w tle i sprawdzam komputer co kilka minut", _profile.WatchInBackground, value =>
        {
            _profile.WatchInBackground = value;
            if (value) _careTimer.Start(); else _careTimer.Stop();
        }));
        card.Children.Add(AutomationSwitch("Sprzątam pliki tymczasowe bez pytania", _profile.AllowTempCleanup, value => _profile.AllowTempCleanup = value));
        card.Children.Add(AutomationSwitch("Pilnuję autostartu i sam zgłaszam zbędne wpisy", _profile.AllowStartupTuning, value => _profile.AllowStartupTuning = value));
        card.Children.Add(AutomationSwitch("Pilnuję planu zasilania pod mój priorytet", _profile.AllowPowerTuning, value => _profile.AllowPowerTuning = value));
        card.Children.Add(Text("Zmiany wymagające zgody Windows (UAC) zawsze pokażę przed wykonaniem, a oryginalne wartości zapiszę do przywrócenia.", 10, "TextSecondaryBrush", margin: new Thickness(0, 11, 0, 0), lineHeight: 16));
        return card;
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

    private CheckBox AutomationSwitch(string label, bool value, Action<bool> onChanged)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8)
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.Checked += (_, _) => { onChanged(true); BlessedProfileStore.Save(_profile); };
        box.Unchecked += (_, _) => { onChanged(false); BlessedProfileStore.Save(_profile); };
        return box;
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
        return card;
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

            RenderFindings();
            UpdateCareBadge();
            SetCareStatus(DescribeWatchStatus());
            if (_currentPage == "care")
                BlessedMessage.Text = _lastReport.Headline;
            else if (_lastReport.ProblemCount > 0)
                BlessedMessage.Text = $"{_lastReport.Headline} Zajrzyj do zakładki „Blessed czuwa” — mam gotowe rozwiązania.";
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
        SetCareStatus("Sprzątam pliki tymczasowe…");
        try
        {
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
        _pingResult.Text = "Wysyłam jedno zapytanie ping…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
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
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 2);
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
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 2);
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
