using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceSnapshot? _snapshot;
    private string _currentPage = "gaming";
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

    public MainWindow(DeviceSnapshot? initialSnapshot, bool isPortable, bool skipUpdateCheck)
    {
        InitializeComponent();
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
        RenderCurrentPage();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            FooterStatusText.Text = "Odczytuję podstawowe parametry urządzenia lokalnie…";
            try
            {
                _snapshot = await SystemSnapshotService.CaptureAsync(_lifetime.Token);
                HeaderSummary.Text = $"{_snapshot.OperatingSystem} · {_snapshot.TotalMemoryGb:0.#} GB RAM";
                FooterStatusText.Text = "Skan lokalny zakończony · niczego nie zmieniono";
                RenderCurrentPage();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                FooterStatusText.Text = "Nie udało się odczytać wszystkich parametrów. Możesz nadal korzystać z aplikacji.";
                UpdateStatusText.Text = ex.Message;
            }
        }

        if (!_skipUpdateCheck)
            await CheckForUpdatesAsync(showUpToDateMessage: false);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var nextPage = button.Name switch
        {
            nameof(ProcessesNavButton) => "processes",
            nameof(StartupNavButton) => "startup",
            nameof(PowerNavButton) => "power",
            nameof(ConnectionsNavButton) => "connections",
            nameof(ProposalsNavButton) => "proposals",
            nameof(PersonalizationNavButton) => "personalization",
            _ => "gaming"
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
        PageHost.Children.Clear();
        var page = _currentPage switch
        {
            "processes" => BuildProcessesPage(),
            "startup" => BuildStartupPage(),
            "power" => BuildPowerPage(),
            "connections" => BuildConnectionsPage(),
            "proposals" => BuildProposalsPage(),
            "personalization" => BuildPersonalizationPage(),
            _ => BuildGamingPage()
        };
        PageHost.Children.Add(page);
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

    private UIElement BuildGamingPage()
    {
        var page = NewPage("STREFA GRACZA", "Czuwanie, które nie przejmuje steru.",
            "Blessed może lokalnie obserwować ogólne użycie procesora i pamięci podczas gry. To nie jest pomiar FPS, nie identyfikuje procesów gry i nie zamyka aplikacji.");

        var monitor = NewPanel();
        monitor.Children.Add(Text("Tryb czuwania Blessed", 17, "TextPrimaryBrush", FontWeights.SemiBold));
        monitor.Children.Add(Text("Pomiar startuje dopiero po Twoim kliknięciu i odświeża się co 2 sekundy. Zatrzymaj go w dowolnym momencie.", 12, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 14)));

        var controls = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        _monitoringStatus = Text("Czuwanie wyłączone · nic nie jest monitorowane", 11, "TextSecondaryBrush");
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
        monitor.Children.Add(Text("Przy dużym obciążeniu pokażę Ci odczyt — nie ubiję procesu ani nie obiecam braku przycięć. Procesy systemowe, zabezpieczenia, sterowniki i aktualizacje zostają nietknięte.", 11, "TextSecondaryBrush", margin: new Thickness(0, 13, 0, 0)));
        page.Children.Add(WrapPanel(monitor));

        var hardware = NewPanel();
        hardware.Children.Add(Text("O TYM URZĄDZENIU", 10, "AccentBrush", FontWeights.Bold, new Thickness(0, 0, 0, 12)));
        var specs = new UniformGrid { Columns = 2, Rows = 2 };
        specs.Children.Add(SpecCard("PROCESOR", _snapshot?.ProcessorName ?? "Odczyt w toku"));
        specs.Children.Add(SpecCard("PAMIĘĆ", _snapshot is null ? "—" : $"{_snapshot.TotalMemoryGb:0.#} GB RAM · {_snapshot.AvailableMemoryGb:0.#} GB dostępne"));
        specs.Children.Add(SpecCard("KARTA GRAFICZNA", _snapshot?.GraphicsAdapters ?? "Odczyt w toku"));
        specs.Children.Add(SpecCard("WINDOWS", _snapshot is null ? "—" : $"{_snapshot.OperatingSystem} · kompilacja {_snapshot.OperatingSystemBuild}"));
        hardware.Children.Add(specs);
        if (_snapshot?.SystemDriveFreeGb is { } freeGb)
            hardware.Children.Add(Text($"Dysk systemowy: około {freeGb:0.#} GB wolnego miejsca. To odczyt, nie test szybkości.", 11, "TextSecondaryBrush", margin: new Thickness(0, 13, 0, 0)));
        page.Children.Add(WrapPanel(hardware));

        var note = NewPanel();
        note.Children.Add(Text("Ważne", 13, "GoldBrush", FontWeights.SemiBold));
        note.Children.Add(Text("Procesów nie zamykam, a usług, zabezpieczeń, sterowników i Windows Update nie wyłączam. Autostart bieżącego konta oraz wybrane ukryte opcje zasilania można zmieniać osobno — z opisem skutków, zgodą użytkownika i możliwością przywrócenia zapisanych wartości.", 11, "TextSecondaryBrush", margin: new Thickness(0, 6, 0, 0)));
        page.Children.Add(WrapPanel(note));
        return page;
    }

    private UIElement BuildProcessesPage()
    {
        var page = NewPage("PROCESY", "Zobacz, co naprawdę zajmuje zasoby.",
            "Lokalny podgląd CPU, pamięci i czasu uruchomienia każdego procesu. Lista odświeża się co 2 sekundy tylko wtedy, gdy ta karta jest otwarta; nie zamyka ani nie zmienia priorytetu żadnego procesu.");
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
        safety.Children.Add(Text("Pełny obraz, bez ryzykownego „Ubij proces”", 13, "GoldBrush", FontWeights.SemiBold));
        safety.Children.Add(Text("Dostęp do chronionych procesów Windows może być ograniczony przez system. Blessed nie kończy zadań, nie dotyka zabezpieczeń ani nie obiecuje wzrostu FPS na podstawie samego zużycia CPU.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
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
        var page = NewPage("AUTOSTART", "Wybierz, co startuje razem z Windowsem.",
            "Lista obejmuje wyłącznie wpisy Run i RunOnce bieżącego użytkownika. Wyłączenie zapisuje kopię i usuwa tylko rejestrację autostartu — nie zamyka programu ani nie usuwa pliku. Każdą pozycję można przywrócić.");
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
        note.Children.Add(Text("Zakres zmian", 12, "GoldBrush", FontWeights.SemiBold));
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
            FooterStatusText.Text = entry.IsEnabled ? "Autostart wyłączono · kopia do przywrócenia została zachowana" : "Autostart przywrócono · program nie został uruchomiony";
            RenderCurrentPage();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Blessed Optimizer — autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private UIElement BuildPowerPage()
    {
        var page = NewPage("UKRYTE OPCJE ZASILANIA", "Ustawienia planu bez polowania po Panelu sterowania.",
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
        summary.Children.Add(Text("Zmiana otwiera osobny monit UAC dla zapisu systemowego. Blessed nie zmienia limitów temperatury, nie podkręca sprzętu i nie wyłącza zabezpieczeń.", 10, "GoldBrush", margin: new Thickness(0, 8, 0, 0)));
        page.Children.Add(WrapPanel(summary));

        if (snapshot.Settings.Count == 0)
        {
            var empty = NewPanel();
            empty.Children.Add(Text("Ten komputer nie udostępnił obsługiwanych ukrytych ustawień planu.", 11, "TextSecondaryBrush"));
            page.Children.Add(WrapPanel(empty));
        }
        foreach (var state in snapshot.Settings)
            page.Children.Add(BuildPowerSettingCard(snapshot, state));

        var note = NewPanel();
        note.Children.Add(Text("Cofnięcie zmian", 12, "GoldBrush", FontWeights.SemiBold));
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
        var page = NewPage("POŁĄCZENIA", "Najpierw sprawdzę. Niczego nie przestawię.",
            "Lista kart pochodzi z Windows i jest odczytywana lokalnie. Pokazuję Wi-Fi/Ethernet oraz Bluetooth PAN, jeśli Windows wystawia go jako adapter sieciowy. Nie skanuję ani nie paruję urządzeń Bluetooth. Test ping wysyła jedno zapytanie do 1.1.1.1 tylko po kliknięciu.");

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
        testCard.Children.Add(Text("Test mierzy tylko odpowiedź ICMP serwera 1.1.1.1. Nie resetuje Wi-Fi, DNS ani ustawień TCP. Sieć może blokować ping.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 12)));
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
        safety.Children.Add(Text("Naprawa sieci wymaga Twojej zgody", 13, "GoldBrush", FontWeights.SemiBold));
        safety.Children.Add(Text("Reset adaptera, zmiana DNS lub ustawień TCP może przerwać połączenie. Blessed w tej wersji jedynie diagnozuje i objaśnia — nie stosuje zmian w systemie.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(safety));
        return page;
    }

    private UIElement BuildProposalsPage()
    {
        var page = NewPage("PROPOZYCJE", "Pomysły do Twojej decyzji.",
            "Blessed nie wyłącza procesów ani nie zmienia ukrytych ustawień w tle. Poniższe przyciski otwierają odpowiednią stronę Ustawień Windows; o każdej zmianie decydujesz tam sam.");

        if (_snapshot is { TotalMemoryGb: > 0 and < 8 })
            page.Children.Add(ProposalCard("Mało pamięci RAM wykrytej", $"Windows widzi około {_snapshot.TotalMemoryGb:0.#} GB RAM. Zamykaj tylko aplikacje, które sam rozpoznajesz i których teraz nie potrzebujesz. Nie zamykaj procesów systemu ani zabezpieczeń.", "Otwórz wskazówki Windows", null));
        if (_snapshot?.SystemDriveFreeGb is { } freeGb && freeGb < 20)
            page.Children.Add(ProposalCard("Niewiele wolnego miejsca na dysku systemowym", $"Pozostało około {freeGb:0.#} GB. Zanim usuniesz pliki, sprawdź je i zachowaj kopię. Blessed niczego nie usuwa.", "Otwórz Czujnik pamięci", "ms-settings:storagesense"));

        page.Children.Add(ProposalCard("Przejrzyj aplikacje uruchamiane z Windowsem", "Możesz sam wyłączyć autostart aplikacji, które rozpoznajesz i nie są Ci potrzebne od razu. Efekt zależy od tego, co faktycznie uruchamia Twój komputer.", "Otwórz Autostart", "ms-settings:startupapps"));
        page.Children.Add(ProposalCard("Sprawdź Tryb gry Windows", "Tryb gry jest ustawieniem systemowym. Przeczytaj opis w Windows i zdecyduj, czy pasuje do Twoich gier.", "Otwórz Tryb gry", "ms-settings:gaming-gamemode"));
        page.Children.Add(ProposalCard("Zadbaj o aktualne kopie ważnych plików", "Przed większymi zmianami systemowymi przygotuj kopię plików, których nie chcesz stracić. Blessed nie tworzy kopii ani nie zmienia ustawień odzyskiwania.", "Otwórz Kopię zapasową", "ms-settings:backup"));
        return page;
    }

    private UIElement BuildPersonalizationPage()
    {
        var page = NewPage("PERSONALIZACJA WINDOWS", "Komputer, który pasuje do Ciebie.",
            "Wybór motywu i akcentu zmienia tylko wygląd Blessed Optimizer. Ustawień systemowych Windows ta strona nie zapisuje.");

        var appThemeCard = NewPanel();
        appThemeCard.Children.Add(Text("Wygląd samego programu", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        appThemeCard.Children.Add(Text("Wybierz motyw aplikacji. Zmiana obowiązuje tylko w bieżącej sesji.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 13)));
        var modes = new WrapPanel();
        modes.Children.Add(ChoiceButton("Ciemny", () => ApplyTheme("dark", null)));
        modes.Children.Add(ChoiceButton("Jasny", () => ApplyTheme("light", null)));
        appThemeCard.Children.Add(modes);
        appThemeCard.Children.Add(Text("Kolor akcentu", 12, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 16, 0, 8)));
        var colors = new WrapPanel();
        colors.Children.Add(ChoiceButton("Błękit", () => ApplyTheme(null, "sky")));
        colors.Children.Add(ChoiceButton("Złoto", () => ApplyTheme(null, "gold")));
        colors.Children.Add(ChoiceButton("Fiolet", () => ApplyTheme(null, "violet")));
        colors.Children.Add(ChoiceButton("Mięta", () => ApplyTheme(null, "mint")));
        appThemeCard.Children.Add(colors);
        page.Children.Add(WrapPanel(appThemeCard));

        var previewCard = NewPanel();
        previewCard.Children.Add(Text("Podgląd ustawień Windows", 16, "TextPrimaryBrush", FontWeights.SemiBold));
        previewCard.Children.Add(Text("Prawdziwe ustawienia systemowe pozostają bez zmian. Jeśli chcesz je obejrzeć, otwórz Ustawienia Windows samodzielnie.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 12)));
        previewCard.Children.Add(PreviewPanel());
        var openSettings = new Button { Content = "Otwórz ustawienia personalizacji Windows", Style = (Style)FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        openSettings.Click += (_, _) => OpenWindowsSettings("ms-settings:personalization");
        previewCard.Children.Add(openSettings);
        page.Children.Add(WrapPanel(previewCard));

        var privacy = NewPanel();
        privacy.Children.Add(Text("Twoje wybory nie opuszczają aplikacji", 13, "GoldBrush", FontWeights.SemiBold));
        privacy.Children.Add(Text("Diagnostyka działa lokalnie. Motyw i akcent nie są wysyłane na serwer i nie zapisują zmian w Windows.", 11, "TextSecondaryBrush", margin: new Thickness(0, 5, 0, 0)));
        page.Children.Add(WrapPanel(privacy));
        return page;
    }

    private static StackPanel NewPage(string kicker, string title, string description)
    {
        var page = new StackPanel();
        var heading = new StackPanel { Margin = new Thickness(0, 5, 0, 18) };
        heading.Children.Add(Text(kicker, 10, "AccentBrush", FontWeights.Bold));
        heading.Children.Add(Text(title, 27, "TextPrimaryBrush", FontWeights.SemiBold, new Thickness(0, 7, 0, 0)));
        heading.Children.Add(Text(description, 12, "TextSecondaryBrush", margin: new Thickness(0, 8, 0, 0), lineHeight: 19));
        page.Children.Add(heading);
        return page;
    }

    private static StackPanel NewPanel()
    {
        var panel = new StackPanel();
        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 14),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1)
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

    private static Border SpecCard(string title, string value)
    {
        var content = new StackPanel();
        content.Children.Add(Text(title, 9, "AccentBrush", FontWeights.Bold));
        content.Children.Add(Text(value, 11, "TextPrimaryBrush", FontWeights.Medium, new Thickness(0, 5, 0, 0)));
        var border = new Border { Child = content, Padding = new Thickness(12), Margin = new Thickness(0, 0, 9, 9), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
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
            FooterStatusText.Text = "Czuwanie działa lokalnie · aplikacje i procesy nie są zmieniane";
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
        if (_monitoringStatus is not null) _monitoringStatus.Text = "Czuwanie wyłączone · nic nie jest monitorowane";
        FooterStatusText.Text = "Czuwanie zatrzymane · zmiany systemowe wymagają osobnej zgody";
        if (_cpuValue is not null) _cpuValue.Text = "—";
        if (_memoryValue is not null) _memoryValue.Text = "—";
        if (_cpuBar is not null) _cpuBar.Value = 0;
        if (_memoryBar is not null) _memoryBar.Value = 0;
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
        _cpuBar.Value = usage.CpuPercent ?? 0;
        _memoryBar.Value = usage.MemoryPercent;
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
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            var result = await UpdateService.CheckLatestAsync(current, _lifetime.Token);
            UpdateStatusText.Text = result.Message;
            if (result.HasUpdate)
            {
                var choice = MessageBox.Show(this,
                    $"Jest dostępna aktualizacja Blessed Optimizer {result.LatestVersion}.\n\nPobrać instalator z GitHub Releases, sprawdzić jego SHA-256 i zaktualizować kopię w Twoim profilu? Program uruchomi się ponownie.\n\nNie zmieni to ustawień Windows.",
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
            resources["SurfaceBrush"] = BrushFrom("#FFFFFF");
            resources["SurfaceRaisedBrush"] = BrushFrom("#E4EEF8");
            resources["BorderBrush"] = BrushFrom("#C7D8E8");
            resources["TextPrimaryBrush"] = BrushFrom("#142A40");
            resources["TextSecondaryBrush"] = BrushFrom("#526B83");
        }
        else if (mode == "dark")
        {
            resources["WindowBackgroundBrush"] = BrushFrom("#07172C");
            resources["SurfaceBrush"] = BrushFrom("#10243D");
            resources["SurfaceRaisedBrush"] = BrushFrom("#172F4E");
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

    private static SolidColorBrush BrushFrom(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6) throw new FormatException("Nieprawidłowy kolor motywu.");
        var red = byte.Parse(value[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var green = byte.Parse(value[2..4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var blue = byte.Parse(value[4..6], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromRgb(red, green, blue));
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _watchTimer.Stop();
        _processTimer.Stop();
        _lifetime.Cancel();
    }
}
