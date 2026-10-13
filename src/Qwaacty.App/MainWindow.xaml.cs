using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Qwaacty.Core;
using WinForms = System.Windows.Forms;

namespace Qwaacty.App;

public partial class MainWindow : Window
{
    private const int TypingToggleHotkeyId = 7142;
    private const int SelectionTriggerHotkeyId = 7143;
    private const int SelectionToggleHotkeyId = 7144;
    private const int ScreenshotHotkeyId = 7145;
    private const int SelectionAutoToggleHotkeyId = 7146;
    private const int ProfessionalHotkeyId = 7148;
    private const string LexiconDownloadUrl = "https://github.com/liangmumumu/qwaacty/releases/download/dicts-v1/lexicon.ttlx";
    private const string EedictDownloadUrl = "https://github.com/liangmumumu/qwaacty/releases/download/dicts-v1/eedict.json";
    private const int TypingReadTimeoutMilliseconds = 1500;
    private const int TypingFallbackStableMilliseconds = 650;
    private const int TypingTranslatePauseMilliseconds = 500;
    private const int MaxTypingRetainedRows = 8;

    private readonly WindowsInputMonitor _inputMonitor = new();
    private readonly MouseSelectionMonitor _mouseMonitor = new();
    private readonly TypingBuffer _typingBuffer = new();
    private readonly LookupWindow _lookupWindow = new();
    private readonly DispatcherTimer _inputTimer;
    private readonly DispatcherTimer _popupAutoCloseTimer;
    private readonly DispatcherTimer _typingFallbackTimer;
    private readonly DispatcherTimer _hookWatchdogTimer;
    private readonly DispatcherTimer _appearanceSaveTimer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _aiHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    // Dictionary downloads are large (≈ 34 MB) and can be slow; a long timeout keeps
    // the streamed copy alive without affecting the request clients above.
    private readonly HttpClient _downloadClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly OpenAiCompatibleTranslator _aiTranslator;
    private readonly TranslationCache _translationCache = new();
    private IDictionaryLookup _dictionaryLookup;
    private ILocalDictionaryProvider _localProvider;
    private ProfessionalDictionary? _professional;
    private ProfessionalEntry? _professionalCurrent;
    private bool _downloadInFlight;
    private readonly AppSettings _settings;
    private HwndSource? _windowSource;
    private WindowsHotkey? _typingHotkey;
    private WindowsHotkey? _selectionHotkey;
    private WindowsHotkey? _selectionToggleHotkey;
    private WindowsHotkey? _selectionAutoToggleHotkey;
    private WindowsHotkey? _screenshotHotkey;
    private WindowsHotkey? _professionalHotkey;
    private ScreenshotOverlayWindow? _screenshotOverlay;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _typingTrayItem;
    private WinForms.ToolStripMenuItem? _selectionTrayItem;
    private CancellationTokenSource? _lookupCancellation;
    private bool _loadingSettings = true;
    private bool _fontsLoaded;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _readInFlight;
    private Task<FocusedTextReadResult>? _pendingUiaRead;
    private bool _trayIconFailed;
    private bool _updatingTray;
    private long _uiaSuspendUntilTicks;
    private DateTime _lastCompatHintUtc = DateTime.MinValue;
    private string? _pendingHotkeyError;
    private DateTime _lastSelectionLookupUtc = DateTime.MinValue;
    private DateTime _popupShownAtUtc = DateTime.MinValue;
    private DateTime _lastTypingActivityUtc = DateTime.MinValue;
    private DateTime _onlineBackoffUntilUtc = DateTime.MinValue;
    private DateTime _popupExpiresAtUtc = DateTime.MinValue;
    private static readonly bool TraceSelection =
        Environment.GetEnvironmentVariable("QWAACTY_TRACE_SELECTION") == "1";
    private DateTime _lastHookRefreshUtc = DateTime.MinValue;
    private long _screenshotSuppressGesturesUntilTicks;
    private List<QueryCandidate>? _pendingTypingFallback;
    private Rect? _typingAnchor;
    private Rect? _typingInputBounds;
    private readonly List<LookupWindow.RetainedRow> _typingRetained = [];
    private (string Word, string Gloss)? _typingPendingRetain;
    private string? _typingLastToken;
    private string? _typingLastGloss;
    private bool _typingDetailsShown;

    private static readonly char[] TypingSentenceEnders = [',', '.', '，', '。'];
    private readonly Dictionary<string, (DateTime ExpiresUtc, string Message)> _recentMisses = new();
    private PopupTrigger _popupTrigger = PopupTrigger.None;

    private enum PopupTrigger
    {
        None,
        Typing,
        SelectionAuto,
        SelectionHotkey,
        Screenshot
    }

    public MainWindow()
    {
        var startupNotes = new List<string>();
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        try
        {
            _settings = SettingsStore.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _settings = new AppSettings();
            startupNotes.Add($"读取设置失败，暂用默认值：{exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(_settings.Hotkey))
        {
            _settings.Hotkey = "Ctrl+Alt+Shift+T";
        }

        if (string.IsNullOrWhiteSpace(_settings.SelectionHotkey))
        {
            _settings.SelectionHotkey = "Ctrl+Alt+Shift+D";
        }

        if (string.IsNullOrWhiteSpace(_settings.SelectionToggleHotkey))
        {
            _settings.SelectionToggleHotkey = "Ctrl+Alt+Shift+S";
        }

        if (string.IsNullOrWhiteSpace(_settings.ScreenshotHotkey))
        {
            _settings.ScreenshotHotkey = "Ctrl+Alt+Shift+X";
        }

        if (string.IsNullOrWhiteSpace(_settings.SelectionAutoToggleHotkey))
        {
            _settings.SelectionAutoToggleHotkey = "Ctrl+Alt+Shift+A";
        }

        if (string.IsNullOrWhiteSpace(_settings.ProfessionalHotkey))
        {
            _settings.ProfessionalHotkey = "Ctrl+Alt+Shift+P";
        }

        try
        {
            StartupRegistration.MigrateLegacyValue();
            _settings.StartWithWindows = StartupRegistration.IsEnabled();
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            startupNotes.Add($"读取开机启动设置失败：{exception.Message}");
        }

        var (localProvider, lexiconStatus) = LoadLocalProvider();
        _localProvider = localProvider;
        _professional = LoadProfessionalDictionary();
        _dictionaryLookup = new DictionaryLookup(
            localProvider,
            new MyMemoryLookupProvider(_httpClient));
        _aiTranslator = new OpenAiCompatibleTranslator(_aiHttpClient);
        _inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _inputTimer.Tick += InputTimer_OnTick;
        _popupAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _popupAutoCloseTimer.Tick += PopupAutoCloseTimer_OnTick;
        _typingFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _typingFallbackTimer.Tick += TypingFallbackTimer_OnTick;
        _hookWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _hookWatchdogTimer.Tick += (_, _) => RefreshMonitors(force: true);
        // Debounce for appearance sliders: dragging fires dozens of value changes and
        // writing settings.json on each one is both slow and wears the disk. The UI and
        // popup preview still update on every tick; only the file write is coalesced.
        _appearanceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _appearanceSaveTimer.Tick += (_, _) =>
        {
            _appearanceSaveTimer.Stop();
            SaveSettingsOrReport();
        };
        _inputMonitor.InputActivity += InputMonitor_OnInputActivity;
        _inputMonitor.CharacterTyped += OnCharacterTyped;
        _inputMonitor.BackspaceTyped += OnBackspaceTyped;
        _inputMonitor.ContextResetRequested += () => _typingBuffer.Clear();
        _inputMonitor.ForegroundChanged += () => DispatchInput(() =>
        {
            if (_popupTrigger != PopupTrigger.Typing)
            {
                return;
            }

            // Only a real switch away from the typing application closes the box; a focus
            // change to our own popup (e.g. clicking it) must not.
            var foreground = GetForegroundWindow();
            var popupHandle = new WindowInteropHelper(_lookupWindow).Handle;
            if (foreground != popupHandle)
            {
                ClearTypingSession(hidePopup: true);
            }
        });
        _inputMonitor.LineTerminated += () => DispatchInput(() => ClearTypingSession(hidePopup: true));
        _mouseMonitor.PointerPressed += () => DispatchInput(OnMousePointerPressed);
        _mouseMonitor.DragSelectCompleted += (start, end, hookTicks) => DispatchInput(() => OnDragSelectCompleted(start, end, hookTicks));
        _lookupWindow.SizeAdjusted += OnLookupWindowSizeAdjusted;
        _lookupWindow.CloseRequested += () => DispatchInput(OnLookupWindowCloseRequested);
        _lookupWindow.AiEnhanceRequested += () => DispatchInput(OnProfessionalAiEnhanceRequested);
        _lookupWindow.WikiLinkRequested += url => DispatchInput(() => OpenWikiLink(url));
        // Create the popup's window handle eagerly; the first Show() afterwards is cheap,
        // which keeps the UI thread from stalling long enough for Windows to drop the
        // low-level mouse hook.
        new WindowInteropHelper(_lookupWindow).EnsureHandle();
        Loaded += OnWindowLoaded;

        InitializeControls();
        _loadingSettings = false;
        DiagnosticsLog.Enabled = _settings.DiagnosticsEnabled;
        var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        DiagnosticsLog.Write("START", $"version={appVersion} typing={_settings.TypingLookupEnabled} selectionHotkey={_settings.SelectionHotkeyEnabled} selectionAuto={_settings.SelectionLookupAuto} compat={_settings.CompatibilityMode} online={_settings.AllowOnlineLookup} ai={_settings.AiEnabled}");
        startupNotes.Add(lexiconStatus);
        startupNotes.Add("勾选上方任一模块后开始工作；关闭窗口后程序驻留托盘。");
        SetStatus(string.Join(" ", startupNotes));
        UpdateMonitoring();
        _hookWatchdogTimer.Start();
        // Diagnostic heartbeat: logs every UI-thread stall of >= 40 ms so stutter reports
        // can be correlated with concrete busy windows. Only runs while the diagnostics
        // setting is on; the background thread stops when the dispatcher shuts down.
        if (DiagnosticsLog.Enabled)
        {
            new UiStallProbe(Dispatcher).Start();
        }
    }

    private void InitializeControls()
    {
        TypingCheckBox.IsChecked = _settings.TypingLookupEnabled;
        SelectionCheckBox.IsChecked = _settings.SelectionHotkeyEnabled;
        SelectionAutoCheckBox.IsChecked = _settings.SelectionLookupAuto;
        HotkeyAutoCloseCheckBox.IsChecked = _settings.SelectionHotkeyAutoClose;
        OnlineCheckBox.IsChecked = _settings.AllowOnlineLookup;
        StartupCheckBox.IsChecked = _settings.StartWithWindows;
        CompatibilityCheckBox.IsChecked = _settings.CompatibilityMode;
        DiagnosticsCheckBox.IsChecked = _settings.DiagnosticsEnabled;
        AiEnabledCheckBox.IsChecked = _settings.AiEnabled;
        AiBaseUrlTextBox.Text = _settings.AiBaseUrl;
        AiModelTextBox.Text = _settings.AiModel;
        AiApiKeyBox.Password = _settings.AiApiKey;
        AiStreamingCheckBox.IsChecked = _settings.AiStreaming;
        AiContextCheckBox.IsChecked = _settings.AiContextEnhance;
        AiContextCheckBox.IsEnabled = _settings.SelectionHotkeyEnabled || _settings.SelectionLookupAuto;
        AiPresetComboBox.ItemsSource = AiPresets.Select(preset => preset.Name).ToArray();
        AiPresetComboBox.SelectedIndex = AiPresets
            .Select((preset, index) => (preset, index))
            .Where(item => string.Equals(item.preset.BaseUrl, _settings.AiBaseUrl, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(AiPresets.Length - 1)
            .First();
        HotkeyTextBox.Text = _settings.Hotkey;
        SelectionHotkeyTextBox.Text = _settings.SelectionHotkey;
        SelectionToggleHotkeyTextBox.Text = _settings.SelectionToggleHotkey;
        SelectionAutoToggleHotkeyTextBox.Text = _settings.SelectionAutoToggleHotkey;
        ScreenshotHotkeyTextBox.Text = _settings.ScreenshotHotkey;
        ProfessionalCheckBox.IsChecked = _settings.ProfessionalModeEnabled;
        ProfessionalHotkeyTextBox.Text = _settings.ProfessionalHotkey;
        UpdateLibraryStatus();
        PopupWidthTextBox.Text = _settings.PopupWidth.ToString("F0");
        PopupHeightTextBox.Text = _settings.PopupMaxHeight.ToString("F0");
        BackgroundColorEditor.SetColor(TryParsePopupColor(_settings.PopupBackgroundColor, out var background)
            ? background
            : Color.FromRgb(0x1E, 0x1E, 0x1E));
        TextColorEditor.SetColor(TryParsePopupColor(_settings.PopupTextColor, out var textColor)
            ? textColor
            : Color.FromRgb(0xD4, 0xD4, 0xD4));
        BackgroundColorEditor.ColorChanged += OnBackgroundColorChanged;
        TextColorEditor.ColorChanged += OnTextColorChanged;
        UpdateHomeSummary();

        FontSizeSlider.Value = Math.Clamp(_settings.PopupFontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        OpacitySlider.Value = Math.Clamp(_settings.PopupOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        UpdateAppearanceLabels();
        ApplyPopupAppearance();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        InitializeTray();
        if (_fontsLoaded)
        {
            return;
        }

        _fontsLoaded = true;
        var previousLoadingState = _loadingSettings;
        _loadingSettings = true;
        FontComboBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(family => family.Source).ToArray();
        var fonts = FontComboBox.Items.OfType<FontFamily>().ToArray();
        FontComboBox.SelectedItem =
            fonts.FirstOrDefault(family => string.Equals(family.Source, _settings.PopupFontFamily, StringComparison.OrdinalIgnoreCase)) ??
            fonts.FirstOrDefault(family => family.Source.Contains("Microsoft YaHei", StringComparison.OrdinalIgnoreCase)) ??
            fonts.FirstOrDefault();
        _loadingSettings = previousLoadingState;
        ApplyPopupAppearance();
    }

    private static (ILocalDictionaryProvider Provider, string Status) LoadLocalProvider()
    {
        var lexiconPath = Path.Combine(AppContext.BaseDirectory, "dicts", "lexicon.ttlx");
        if (LexiconDictionaryProvider.TryLoad(lexiconPath, out var lexicon, out var error) && lexicon is not null)
        {
            return (lexicon, $"本地词库已加载：{lexicon.WordCount:N0} 个词条。");
        }

        return (new LocalDictionaryProvider(), $"未加载本地词库（{error}），可在「词库」页下载；暂用演示词条。");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource.AddHook(WindowProcedure);
        TryEnableDarkTitleBar(handle);

        _typingHotkey = new WindowsHotkey(handle, TypingToggleHotkeyId);
        _selectionHotkey = new WindowsHotkey(handle, SelectionTriggerHotkeyId);
        _selectionToggleHotkey = new WindowsHotkey(handle, SelectionToggleHotkeyId);
        _selectionAutoToggleHotkey = new WindowsHotkey(handle, SelectionAutoToggleHotkeyId);
        _screenshotHotkey = new WindowsHotkey(handle, ScreenshotHotkeyId);
        _professionalHotkey = new WindowsHotkey(handle, ProfessionalHotkeyId);
        try
        {
            _typingHotkey.Register(_settings.Hotkey);
            DiagnosticsLog.Write("HOTKEY", $"typing={_settings.Hotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"键入翻译开关快捷键（{_settings.Hotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"typing={_settings.Hotkey} FAIL {exception.Message}");
        }

        try
        {
            _selectionHotkey.Register(_settings.SelectionHotkey);
            DiagnosticsLog.Write("HOTKEY", $"selection={_settings.SelectionHotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"划词触发快捷键（{_settings.SelectionHotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"selection={_settings.SelectionHotkey} FAIL {exception.Message}");
        }

        try
        {
            _selectionToggleHotkey.Register(_settings.SelectionToggleHotkey);
            DiagnosticsLog.Write("HOTKEY", $"selectionToggle={_settings.SelectionToggleHotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"划词开关快捷键（{_settings.SelectionToggleHotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"selectionToggle={_settings.SelectionToggleHotkey} FAIL {exception.Message}");
        }

        try
        {
            _selectionAutoToggleHotkey.Register(_settings.SelectionAutoToggleHotkey);
            DiagnosticsLog.Write("HOTKEY", $"selectionAutoToggle={_settings.SelectionAutoToggleHotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"自动划词开关快捷键（{_settings.SelectionAutoToggleHotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"selectionAutoToggle={_settings.SelectionAutoToggleHotkey} FAIL {exception.Message}");
        }

        try
        {
            _screenshotHotkey.Register(_settings.ScreenshotHotkey);
            DiagnosticsLog.Write("HOTKEY", $"screenshot={_settings.ScreenshotHotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"截图翻译快捷键（{_settings.ScreenshotHotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"screenshot={_settings.ScreenshotHotkey} FAIL {exception.Message}");
        }

        try
        {
            _professionalHotkey.Register(_settings.ProfessionalHotkey);
            DiagnosticsLog.Write("HOTKEY", $"professional={_settings.ProfessionalHotkey} ok");
        }
        catch (Exception exception) when (exception is Win32Exception or FormatException)
        {
            _pendingHotkeyError = $"专业词库快捷键（{_settings.ProfessionalHotkey}）注册失败：{exception.Message}";
            DiagnosticsLog.Write("HOTKEY", $"professional={_settings.ProfessionalHotkey} FAIL {exception.Message}");
        }

        if (_pendingHotkeyError is not null)
        {
            SetStatus(_pendingHotkeyError);
        }
    }

    private void InitializeTray()
    {
        try
        {
            System.Drawing.Icon? icon = null;
            try
            {
                if (Environment.ProcessPath is { } processPath)
                {
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
            }

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Text = "Qwaacty",
                Visible = true
            };
            var menu = new WinForms.ContextMenuStrip();
            var openItem = new WinForms.ToolStripMenuItem("打开主窗口");
            openItem.Click += (_, _) => RestoreMainWindow();
            _typingTrayItem = new WinForms.ToolStripMenuItem("键入实时翻译") { CheckOnClick = true };
            _typingTrayItem.Click += (_, _) =>
            {
                if (!_updatingTray)
                {
                    SetTypingEnabled(_typingTrayItem.Checked, announce: false);
                }
            };
            _selectionTrayItem = new WinForms.ToolStripMenuItem("划词翻译") { CheckOnClick = true };
            _selectionTrayItem.Click += (_, _) =>
            {
                if (!_updatingTray)
                {
                    SetSelectionEnabled(_selectionTrayItem.Checked, announce: false);
                }
            };
            var screenshotItem = new WinForms.ToolStripMenuItem($"截图翻译（{_settings.ScreenshotHotkey}）");
            screenshotItem.Click += (_, _) => StartScreenshotCapture();
            var exitItem = new WinForms.ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(openItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_typingTrayItem);
            menu.Items.Add(_selectionTrayItem);
            menu.Items.Add(screenshotItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
            UpdateTrayChecks();
            if (_pendingHotkeyError is not null)
            {
                try
                {
                    _trayIcon.BalloonTipTitle = "Qwaacty";
                    _trayIcon.BalloonTipText = _pendingHotkeyError;
                    _trayIcon.ShowBalloonTip(5000);
                }
                catch (InvalidOperationException)
                {
                }

                _pendingHotkeyError = null;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Without a tray icon there is no way back: closing the window must exit.
            _trayIconFailed = true;
            SetStatus($"托盘图标初始化失败：{exception.Message}");
        }
    }

    private void UpdateTrayChecks()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _updatingTray = true;
        try
        {
            if (_typingTrayItem is not null)
            {
                _typingTrayItem.Checked = _settings.TypingLookupEnabled;
            }

            if (_selectionTrayItem is not null)
            {
                _selectionTrayItem.Checked = _settings.SelectionHotkeyEnabled;
            }
        }
        finally
        {
            _updatingTray = false;
        }
    }

    private void RestoreMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            try
            {
                _trayIcon.BalloonTipTitle = "Qwaacty";
                _trayIcon.BalloonTipText = "程序仍在后台运行，快捷键保持有效。双击托盘图标可重新打开窗口，右键可退出。";
                _trayIcon.ShowBalloonTip(3000);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Announce(string message)
    {
        if (!IsVisible && _trayIcon is not null)
        {
            try
            {
                _trayIcon.BalloonTipTitle = "Qwaacty";
                _trayIcon.BalloonTipText = message;
                _trayIcon.ShowBalloonTip(1500);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && !_trayIconFailed)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void SetTypingEnabled(bool enabled, bool announce)
    {
        _settings.TypingLookupEnabled = enabled;
        SetCheckboxWithoutEvent(TypingCheckBox, enabled);
        if (!enabled)
        {
            _inputTimer.Stop();
            _typingBuffer.Clear();
            CancelLookup();
            ClearTypingSession(hidePopup: true);
        }

        UpdateMonitoring();
        UpdateTrayChecks();
        var saved = SaveSettingsOrReport();
        var message = enabled ? "键入实时翻译：已开启。" : "键入实时翻译：已关闭。";
        SetStatus(message);
        if (announce)
        {
            Announce(saved ? message : "设置保存失败，本次修改仅在当前运行内有效。");
        }
    }

    private void SetSelectionEnabled(bool enabled, bool announce)
    {
        _settings.SelectionHotkeyEnabled = enabled;
        SetCheckboxWithoutEvent(SelectionCheckBox, enabled);
        AiContextCheckBox.IsEnabled = enabled || _settings.SelectionLookupAuto;
        if (!enabled && _popupTrigger == PopupTrigger.SelectionHotkey)
        {
            CancelLookup();
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }

        UpdateMonitoring();
        UpdateTrayChecks();
        UpdateHomeSummary();
        var saved = SaveSettingsOrReport();
        var message = enabled
            ? $"快捷键划词：已开启（选中文本后按 {_settings.SelectionHotkey} 翻译）。"
            : "快捷键划词：已关闭。";
        SetStatus(message);
        if (announce)
        {
            Announce(saved ? message : "设置保存失败，本次修改仅在当前运行内有效。");
        }
    }

    private void SetSelectionAutoEnabled(bool enabled, bool announce)
    {
        _settings.SelectionLookupAuto = enabled;
        SetCheckboxWithoutEvent(SelectionAutoCheckBox, enabled);
        AiContextCheckBox.IsEnabled = enabled || _settings.SelectionHotkeyEnabled;
        if (!enabled && _popupTrigger == PopupTrigger.SelectionAuto)
        {
            CancelLookup();
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }

        UpdateMonitoring();
        UpdateHomeSummary();
        var saved = SaveSettingsOrReport();
        var message = enabled ? "自动划词翻译：已开启。" : "自动划词翻译：已关闭。";
        SetStatus(message);
        if (announce)
        {
            Announce(saved ? message : "设置保存失败，本次修改仅在当前运行内有效。");
        }
    }

    private bool IsSelectionModeEnabled(bool triggeredByHotkey) =>
        _settings.SelectionLookupEnabled &&
        (triggeredByHotkey ? _settings.SelectionHotkeyEnabled : _settings.SelectionLookupAuto);

    private void UpdateHomeSummary()
    {
        TypingHotkeyHint.Text = $"快捷键开关：{_settings.Hotkey}";
        SelectionAutoHotkeyHint.Text = $"快捷键开关：{_settings.SelectionAutoToggleHotkey}";
        SelectionHotkeyHint.Text =
            $"翻译：{_settings.SelectionHotkey}    开关：{_settings.SelectionToggleHotkey}";
        ProfessionalHint.Text = !_settings.ProfessionalModeEnabled
            ? "已关闭（开启后划词/截图优先查询专业词库；需先在「词库」页下载）"
            : _professional is not null
                ? $"快捷键：{_settings.ProfessionalHotkey} — 划词/截图优先专业词库（{_professional.Count} 条，中英对照+解释+百科链接）"
                : "已开启，但专业词库尚未下载：请到「词库」页下载";
        AiSummaryTextBlock.Text = !_settings.AiEnabled
            ? "AI 翻译已关闭"
            : AiConfigured
                ? $"已启用 · {_settings.AiModel} · {_settings.AiBaseUrl}"
                : "尚未完成配置（前往 Settings 设置服务商、模型和 API Key）";
    }

    private void NavigateHomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Collapsed;
    }

    private void NavigateSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        LibraryView.Visibility = Visibility.Collapsed;
    }

    private void UpdateMonitoring()
    {
        var needKeyboard = _settings.TypingLookupEnabled;
        var needMouse = (_settings.TypingLookupEnabled && _settings.CompatibilityMode) ||
            (_settings.SelectionLookupEnabled && _settings.SelectionLookupAuto);
        try
        {
            if (needKeyboard)
            {
                _inputMonitor.Refresh();
            }
            else
            {
                _inputMonitor.Stop();
                _typingBuffer.Clear();
            }
        }
        catch (Win32Exception exception)
        {
            DiagnosticsLog.Write("MON", $"keyboard start failed: {exception.Message}");
            SetStatus($"键盘监测启动失败：{exception.Message}");
        }

        try
        {
            if (needMouse)
            {
                _mouseMonitor.Refresh();
            }
            else
            {
                _mouseMonitor.Stop();
            }
        }
        catch (Win32Exception exception)
        {
            DiagnosticsLog.Write("MON", $"mouse start failed: {exception.Message}");
            SetStatus($"鼠标监测启动失败：{exception.Message}");
        }

        _inputMonitor.CaptureCharacters = needKeyboard && _settings.CompatibilityMode;
        DiagnosticsLog.Write("MON",
            $"monitors keyboard={_inputMonitor.IsRunning}(want {needKeyboard}) mouse={_mouseMonitor.IsRunning}(want {needMouse})");
    }

    private void RefreshMonitors(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastHookRefreshUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastHookRefreshUtc = now;
        var needKeyboard = _settings.TypingLookupEnabled;
        var needMouse = (_settings.TypingLookupEnabled && _settings.CompatibilityMode) ||
            (_settings.SelectionLookupEnabled && _settings.SelectionLookupAuto);
        try
        {
            if (needMouse)
            {
                // Refresh reinstalls the hook (healing silent removals); Start resurrects
                // a monitor that a previous failed refresh left stopped.
                if (_mouseMonitor.IsRunning)
                {
                    _mouseMonitor.Refresh();
                }
                else
                {
                    _mouseMonitor.Start();
                }
            }
        }
        catch (Win32Exception exception)
        {
            DiagnosticsLog.Write("MON", $"mouse refresh failed: {exception.Message}");
        }

        try
        {
            if (needKeyboard)
            {
                if (_inputMonitor.IsRunning)
                {
                    _inputMonitor.Refresh();
                }
                else
                {
                    _inputMonitor.Start();
                }
            }
        }
        catch (Win32Exception exception)
        {
            DiagnosticsLog.Write("MON", $"keyboard refresh failed: {exception.Message}");
        }
    }

    private void TypingCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            SetTypingEnabled(TypingCheckBox.IsChecked == true, announce: false);
        }
    }

    private void SelectionCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            SetSelectionEnabled(SelectionCheckBox.IsChecked == true, announce: false);
        }
    }

    private void SelectionAutoCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        SetSelectionAutoEnabled(SelectionAutoCheckBox.IsChecked == true, announce: false);
    }

    private void OnlineCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AllowOnlineLookup = OnlineCheckBox.IsChecked == true;
        SaveSettingsOrReport();
    }

    private void StartupCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var shouldEnable = StartupCheckBox.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(shouldEnable);
            _settings.StartWithWindows = shouldEnable;
            SaveSettingsOrReport();
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            SetCheckboxWithoutEvent(StartupCheckBox, !shouldEnable);
            SetStatus($"更新开机启动失败：{exception.Message}");
        }
    }

    private void CompatibilityCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.CompatibilityMode = CompatibilityCheckBox.IsChecked == true;
        UpdateMonitoring();
        if (!_settings.CompatibilityMode)
        {
            _typingBuffer.Clear();
        }

        DiagnosticsLog.Write("SETTINGS", $"compat={_settings.CompatibilityMode}");
        SaveSettingsOrReport();
    }

    private void DiagnosticsCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.DiagnosticsEnabled = DiagnosticsCheckBox.IsChecked == true;
        DiagnosticsLog.Enabled = _settings.DiagnosticsEnabled;
        DiagnosticsLog.Write("SETTINGS", $"diagnostics={_settings.DiagnosticsEnabled}");
        SaveSettingsOrReport();
    }

    private void ApplyHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_typingHotkey, HotkeyTextBox.Text, chord => _settings.Hotkey = chord, "键入翻译开关快捷键");

    private void ApplySelectionHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_selectionHotkey, SelectionHotkeyTextBox.Text, chord => _settings.SelectionHotkey = chord, "划词触发快捷键");

    private void ApplySelectionToggleHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_selectionToggleHotkey, SelectionToggleHotkeyTextBox.Text, chord => _settings.SelectionToggleHotkey = chord, "划词开关快捷键");

    private void ApplySelectionAutoToggleHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_selectionAutoToggleHotkey, SelectionAutoToggleHotkeyTextBox.Text,
            chord => _settings.SelectionAutoToggleHotkey = chord, "自动划词开关快捷键");

    private void ApplyScreenshotHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_screenshotHotkey, ScreenshotHotkeyTextBox.Text, chord => _settings.ScreenshotHotkey = chord, "截图翻译快捷键");

    private void ApplyHotkey(WindowsHotkey? hotkey, string chord, Action<string> persist, string label)
    {
        if (hotkey is null)
        {
            SetStatus("窗口尚未初始化，无法设置快捷键。");
            return;
        }

        var previous = hotkey.CurrentChord;
        try
        {
            hotkey.Unregister();
            hotkey.Register(chord.Trim());
            persist(hotkey.CurrentChord);
            SettingsStore.Save(_settings);
            UpdateHomeSummary();
            SetStatus($"{label}已设置为 {hotkey.CurrentChord}。");
        }
        catch (Exception exception) when (exception is FormatException or Win32Exception or IOException or
                                           UnauthorizedAccessException or SecurityException)
        {
            try
            {
                // previous is empty when the initial registration at startup failed;
                // re-registering an empty chord would throw FormatException and crash.
                if (!string.IsNullOrWhiteSpace(previous))
                {
                    hotkey.Register(previous);
                }
            }
            catch (Exception restoreException) when (restoreException is Win32Exception or FormatException)
            {
                DiagnosticsLog.Write("HOTKEY", $"rollback failed: {restoreException.Message}");
            }

            SetStatus($"设置{label}失败：{exception.Message}");
        }
    }

    private void OnCharacterTyped(char character)
    {
        if (_settings.TypingLookupEnabled && _settings.CompatibilityMode)
        {
            _typingBuffer.Append(character);
        }
    }

    private void OnBackspaceTyped()
    {
        if (_settings.TypingLookupEnabled && _settings.CompatibilityMode)
        {
            _typingBuffer.Backspace();
        }
    }

    private void InputMonitor_OnInputActivity()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _lastTypingActivityUtc = DateTime.UtcNow;
                _typingFallbackTimer.Stop();
                _pendingTypingFallback = null;
                if (!_settings.TypingLookupEnabled)
                {
                    return;
                }

                // The user resumed typing: per the close rules the detail box closes and the
                // just-translated word moves up into the retained strip (whether it really
                // belongs there is decided at the next pause, once the new token is known).
                if (_typingDetailsShown && _typingLastToken is not null)
                {
                    _typingPendingRetain = (_typingLastToken, _typingLastGloss ?? string.Empty);
                    _typingDetailsShown = false;
                    _typingLastToken = null;
                    _typingLastGloss = null;
                    ShowTypingPad();
                }

                if (!_inputTimer.IsEnabled)
                {
                    _inputTimer.Start();
                }
            }, DispatcherPriority.Input);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
    }

    private async void InputTimer_OnTick(object? sender, EventArgs e)
    {
        _inputTimer.Stop();
        if (!_settings.TypingLookupEnabled || _readInFlight)
        {
            return;
        }

        // Do not let the typing pipeline stomp on a selection popup: the hotkey
        // keystrokes themselves trigger this timer, and hiding/replacing the
        // selection result here is what made hotkey translation look "silent".
        // Held chords also auto-repeat their main key, so keep ignoring activity
        // until the chord is released.
        if (DateTime.UtcNow - _lastSelectionLookupUtc < TimeSpan.FromSeconds(2) ||
            _selectionHotkey?.IsChordHeld() == true)
        {
            return;
        }

        // Translate only after a real typing pause: translating while a word is still
        // being typed regularly guesses the wrong target.
        if (DateTime.UtcNow - _lastTypingActivityUtc < TimeSpan.FromMilliseconds(TypingTranslatePauseMilliseconds))
        {
            _inputTimer.Start();
            return;
        }

        string? context = null;
        var caret = 0;
        string? failureMessage = null;
        var uiaStatus = "skipped";
        var passwordBlocked = false;
        _typingAnchor = null;
        _typingInputBounds = null;

        if (_pendingUiaRead is { IsCompleted: false })
        {
            // A previous UI Automation read is still hanging inside a busy app; do not
            // stack another wait — fall back to the compatibility buffer this round.
            uiaStatus = "busy";
        }
        else if (Environment.TickCount64 >= _uiaSuspendUntilTicks)
        {
            _pendingUiaRead = null;
            _readInFlight = true;
            try
            {
                var readTask = Task.Run(FocusedTextReader.Read);
                _pendingUiaRead = readTask;
                var completed = await Task.WhenAny(readTask, Task.Delay(TypingReadTimeoutMilliseconds)) == readTask;
                if (completed)
                {
                    _pendingUiaRead = null;
                }
                else
                {
                    // Keep the reference (no new reads until it resolves) and observe its
                    // eventual exception so it is never left unobserved.
                    _ = readTask.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
                }

                var readResult = completed
                    ? await readTask
                    : new FocusedTextReadResult(false, null, 0, "读取输入内容超时。");
                if (readResult.IsSafe && readResult.Context is not null)
                {
                    context = readResult.Context;
                    caret = readResult.CaretIndex;
                    _typingAnchor = readResult.AnchorBounds;
                    _typingInputBounds = readResult.InputBounds;
                    uiaStatus = "ok";
                }
                else
                {
                    failureMessage = readResult.Message;
                    uiaStatus = completed ? "fail" : "timeout";
                    passwordBlocked = readResult.PasswordBlocked;
                    if (!completed)
                    {
                        // UI Automation is hanging for this foreground app; trust the
                        // compatibility buffer for a while instead of stacking waits.
                        _uiaSuspendUntilTicks = Environment.TickCount64 + 10_000;
                    }
                }
            }
            finally
            {
                _readInFlight = false;
            }
        }

        if (passwordBlocked)
        {
            // Confirmed password field: honour the "no lookup, no network" promise —
            // not even the compatibility buffer may feed a query from here.
            _typingBuffer.Clear();
            CancelLookup();
            _typingFallbackTimer.Stop();
            _pendingTypingFallback = null;
            if (_popupTrigger == PopupTrigger.Typing)
            {
                ClearTypingSession(hidePopup: true);
            }

            DiagnosticsLog.Write("TYPE", "password field: typing lookup suppressed");
            SetStatus("当前为密码输入区域：已暂停查词并清空输入缓冲。");
            return;
        }

        var candidates = new List<QueryCandidate>(2);
        if (context is not null)
        {
            candidates.AddRange(InputContextParser.Parse(context, caret));
        }

        var compatAvailable = _settings.CompatibilityMode && _typingBuffer.Length > 0;
        if (compatAvailable)
        {
            var buffer = _typingBuffer.Snapshot();
            foreach (var candidate in InputContextParser.Parse(buffer, buffer.Length))
            {
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        if (candidates.Count > 0)
        {
            var activeDirection = candidates[0].Direction;
            candidates = candidates.Where(candidate => candidate.Direction == activeDirection).ToList();
        }

        DiagnosticsLog.Write("TYPE",
            $"uia={uiaStatus} compatLen={_typingBuffer.Length} candidates={candidates.Count}");

        var token = candidates.Count > 0 ? candidates[0].Text : null;
        if (TypingSentenceEnded(token, context, caret) && HasTypingSessionContent())
        {
            DiagnosticsLog.Write("TYPE", "sentence end; retained strip cleared");
            ClearTypingSession(hidePopup: true);
            CancelLookup();
            _typingFallbackTimer.Stop();
            _pendingTypingFallback = null;
            SetStatus("已输入逗号/句号：保留栏清空，继续输入开始新一轮翻译。");
            return;
        }

        if (candidates.Count == 0)
        {
            CancelLookup();
            _typingFallbackTimer.Stop();
            _pendingTypingFallback = null;
            // A visible selection popup belongs to the selection module; do not
            // close it here just because the focused app's text yields no words.
            if (_popupTrigger is not (PopupTrigger.SelectionAuto or PopupTrigger.SelectionHotkey))
            {
                if (_typingRetained.Count > 0 || _typingPendingRetain is not null)
                {
                    // Keep the pad alive while the user composes the next word.
                    ShowTypingPad();
                }
                else
                {
                    _lookupWindow.Hide();
                }
            }

            if (!compatAvailable && failureMessage is not null && !_settings.CompatibilityMode &&
                DateTime.UtcNow - _lastCompatHintUtc > TimeSpan.FromSeconds(60))
            {
                _lastCompatHintUtc = DateTime.UtcNow;
                Announce("当前应用无法被直接读取（如微信/QQ），需开启「兼容模式」才能查词。");
            }

            SetStatus(compatAvailable
                ? "兼容模式：未识别到可查词的英文单词或中文片段。"
                : failureMessage ?? "未识别到可查词的英文单词或中文片段。");
            return;
        }

        CancelLookup();
        _typingFallbackTimer.Stop();
        _pendingTypingFallback = null;
        _lookupCancellation = new CancellationTokenSource();
        var cancellationToken = _lookupCancellation.Token;
        SetStatus("正在查词…");

        try
        {
            var responses = await Task.WhenAll(candidates.Select(candidate =>
                _dictionaryLookup.FindAsync(
                    candidate.Text,
                    candidate.Direction,
                    allowOnline: false,
                    cancellationToken)));
            var entries = FlattenEntries(responses);

            if (entries.Length > 0)
            {
                DiagnosticsLog.Write("TYPE", "result entries=" + entries.Length + " error=none");
                MarkTypingResult(candidates[0].Text, ConciseGloss(entries));
                ShowPopupEntries(entries, PopupTrigger.Typing, candidates[0].Text);
                var fuzzy = entries[0].Direction == LookupDirection.EnglishToChinese &&
                    entries.Any(entry => candidates.All(candidate =>
                        !string.Equals(candidate.Text, entry.Query, StringComparison.Ordinal)));
                SetStatus(fuzzy
                    ? $"查到 {entries.Length} 条词条（已按最接近的词条匹配）。"
                    : compatAvailable
                        ? $"查到 {entries.Length} 条词条（兼容模式）。"
                        : $"查到 {entries.Length} 条词条。");
                return;
            }

            // No local hit: wait for a short typing pause before spending AI or
            // online requests, so half-typed words do not burn quotas.
            var sinceActivity = DateTime.UtcNow - _lastTypingActivityUtc;
            if (sinceActivity < TimeSpan.FromMilliseconds(TypingFallbackStableMilliseconds))
            {
                _pendingTypingFallback = candidates.ToList();
                _typingFallbackTimer.Interval = TimeSpan.FromMilliseconds(
                    Math.Max(60, TypingFallbackStableMilliseconds - sinceActivity.TotalMilliseconds + 40));
                _typingFallbackTimer.Start();
                return;
            }

            await RunTypingFallbackAsync(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // async void: an escaping exception would tear the process down.
            DiagnosticsLog.Write("TYPE", $"tick failed: {exception.GetType().Name} {exception.Message}");
            SetStatus($"查词出错：{exception.Message}");
        }
    }

    private async Task RunTypingFallbackAsync(
        IReadOnlyList<QueryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var primary = candidates[0];
        if (_recentMisses.TryGetValue(MissKey(primary), out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            ShowTypingMiss(primary, cached.Message);
            return;
        }

        // Privacy guard: terminal keystrokes can be passwords (sudo / ssh prompts) and
        // UIA cannot flag those fields, so terminal input must never reach AI or the
        // online fallback. The local dictionary still answers normally.
        var terminalForeground = SelectionReader.IsConsoleWindow(GetForegroundWindow());

        var aiAttempted = false;
        if (!terminalForeground && AiConfigured && !cancellationToken.IsCancellationRequested)
        {
            aiAttempted = true;
            SetStatus("本地未收录，正在用 AI 翻译…");
            if (await TryAiTranslationAsync(primary.Text, primary.Direction, PopupTrigger.Typing, cancellationToken))
            {
                SetStatus("AI 翻译完成。");
                var aiGloss = _translationCache.TryGet(primary.Text, primary.Direction, out var cachedText) &&
                              !string.IsNullOrWhiteSpace(cachedText)
                    ? Shorten(cachedText.Trim(), 24)
                    : string.Empty;
                MarkTypingResult(primary.Text, aiGloss);
                return;
            }
        }

        var compatAvailable = _settings.CompatibilityMode && _typingBuffer.Length > 0;
        var onlineAttempted = false;
        string? error = null;
        if (!terminalForeground && _settings.AllowOnlineLookup && DateTime.UtcNow >= _onlineBackoffUntilUtc)
        {
            onlineAttempted = true;
            var responses = await Task.WhenAll(candidates.Select(candidate =>
                _dictionaryLookup.FindAsync(
                    candidate.Text,
                    candidate.Direction,
                    allowOnline: true,
                    cancellationToken)));
            var entries = FlattenEntries(responses);
            error = responses.Select(response => response.Error).FirstOrDefault(message => message is not null);
            if (error is not null)
            {
                NoteOnlineFailure(error);
            }

            if (!_settings.TypingLookupEnabled || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DiagnosticsLog.Write("TYPE", $"result entries={entries.Length} error={error ?? "none"}");
            if (entries.Length > 0)
            {
                MarkTypingResult(candidates[0].Text, ConciseGloss(entries));
                ShowPopupEntries(entries, PopupTrigger.Typing, candidates[0].Text);
                SetStatus(compatAvailable
                    ? $"查到 {entries.Length} 条词条（兼容模式）。"
                    : $"查到 {entries.Length} 条词条。");
                return;
            }
        }
        else if (!terminalForeground && _settings.AllowOnlineLookup)
        {
            error = "在线翻译暂不可用（近期失败过，稍后自动重试）。";
        }

        var message = error ?? (terminalForeground
            ? "终端窗口中仅使用本地词库（不向网络发送终端输入）。"
            : aiAttempted
                ? "AI 翻译未成功；本地词库未收录该词条。"
                : _settings.AllowOnlineLookup
                    ? "本地词库未收录该词条。"
                    : "本地词库未收录该词条（在线兜底已关闭）。");
        DiagnosticsLog.Write("TYPE",
            $"miss len={primary.Text.Length} dir={primary.Direction} ai={aiAttempted} online={onlineAttempted} backoff={DateTime.UtcNow < _onlineBackoffUntilUtc} error={error ?? "none"}");
        ShowTypingMiss(primary, message);
    }

    private async void TypingFallbackTimer_OnTick(object? sender, EventArgs e)
    {
        _typingFallbackTimer.Stop();
        var candidates = _pendingTypingFallback;
        _pendingTypingFallback = null;
        if (candidates is null || !_settings.TypingLookupEnabled)
        {
            return;
        }

        if (DateTime.UtcNow - _lastTypingActivityUtc < TimeSpan.FromMilliseconds(TypingFallbackStableMilliseconds))
        {
            // Typing resumed before the pause was confirmed; the next tick restarts this.
            return;
        }

        if (DateTime.UtcNow - _lastSelectionLookupUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var cancellationToken = _lookupCancellation?.Token ?? CancellationToken.None;
        try
        {
            await RunTypingFallbackAsync(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // async void: an escaping exception would tear the process down.
            DiagnosticsLog.Write("TYPE", $"fallback failed: {exception.GetType().Name} {exception.Message}");
            SetStatus($"查词出错：{exception.Message}");
        }
    }

    private void ShowTypingMiss(QueryCandidate primary, string message)
    {
        SetStatus(message);
        ShowPopupMessage("未收录", $"「{Shorten(primary.Text, 24)}」\n{message}", PopupTrigger.Typing);
        RememberMiss(primary, message);
    }

    private void RememberMiss(QueryCandidate primary, string message)
    {
        var now = DateTime.UtcNow;
        if (_recentMisses.Count > 256)
        {
            foreach (var key in _recentMisses.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            {
                _recentMisses.Remove(key);
            }
        }

        _recentMisses[MissKey(primary)] = (now.AddMinutes(10), message);
    }

    private static string MissKey(QueryCandidate primary) => $"{primary.Direction}|{primary.Text}";

    private void NoteOnlineFailure(string error)
    {
        var backoff = error.Contains("额度", StringComparison.Ordinal)
            ? TimeSpan.FromMinutes(30)
            : error.Contains("超时", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromMinutes(5);
        _onlineBackoffUntilUtc = DateTime.UtcNow + backoff;
        DiagnosticsLog.Write("NET", $"online backoff {backoff.TotalMinutes:F0} min: {error}");
    }

    private static DictionaryEntry[] FlattenEntries(IEnumerable<LookupResponse> responses) =>
        responses.SelectMany(response => response.Entries)
            .DistinctBy(entry => (entry.Query, entry.Direction, string.Join("|", entry.Definitions)))
            .ToArray();

    private void OnLookupWindowCloseRequested()
    {
        // ✕ must end the typing session, otherwise the next keystroke would pop the
        // box right back; for the other triggers a plain hide is enough.
        if (_popupTrigger == PopupTrigger.Typing)
        {
            ClearTypingSession(hidePopup: false);
        }
        else
        {
            _popupTrigger = PopupTrigger.None;
        }
    }

    // ---- 专业词库（电子信息）----

    private static string DictsDirectory => Path.Combine(AppContext.BaseDirectory, "dicts");

    private static ProfessionalDictionary? LoadProfessionalDictionary() =>
        ProfessionalDictionary.TryLoad(Path.Combine(DictsDirectory, "eedict.json"), out _);

    private void SetProfessionalModeEnabled(bool enabled, bool announce)
    {
        _settings.ProfessionalModeEnabled = enabled;
        SetCheckboxWithoutEvent(ProfessionalCheckBox, enabled);
        if (!enabled && _lookupWindow.IsProfessionalMode)
        {
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }

        UpdateHomeSummary();
        var saved = SaveSettingsOrReport();
        var message = enabled
            ? $"专业词库：已开启（划词/截图优先查专业词库；开关 {_settings.ProfessionalHotkey}）。"
            : "专业词库：已关闭。";
        SetStatus(message);
        if (announce)
        {
            Announce(saved ? message : "设置保存失败，本次修改仅在当前运行内有效。");
        }
    }

    private void ProfessionalCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            SetProfessionalModeEnabled(ProfessionalCheckBox.IsChecked == true, announce: false);
        }
    }

    private void ApplyProfessionalHotkeyButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyHotkey(_professionalHotkey, ProfessionalHotkeyTextBox.Text, chord => _settings.ProfessionalHotkey = chord, "专业词库开关快捷键");

    private void ShowProfessionalPopup(ProfessionalEntry entry, PopupTrigger trigger)
    {
        _professionalCurrent = entry;
        _popupTrigger = trigger;
        _popupExpiresAtUtc = DateTime.MinValue;
        _lookupWindow.ShowProfessional(entry);
        UpdatePopupAutoCloseTimer();
        SetStatus($"专业词库：{entry.Term} → {entry.Chinese}");
        DiagnosticsLog.Write("PRO", $"hit term={entry.Term} len={entry.Term.Length}");
    }

    private async void OnProfessionalAiEnhanceRequested()
    {
        var entry = _professionalCurrent;
        if (entry is null)
        {
            return;
        }

        if (!AiConfigured)
        {
            _lookupWindow.ShowAiEnhanceError("未配置 AI：请到设置 → AI 翻译 填写服务商与 API Key。");
            _lookupWindow.CompleteAiEnhance();
            return;
        }

        CancelLookup();
        _lookupCancellation = new CancellationTokenSource();
        var cancellationToken = _lookupCancellation.Token;
        try
        {
            var options = new AiTranslatorOptions(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel);
            var builder = new System.Text.StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var lastUiUpdateTicks = 0L;
            await foreach (var chunk in _aiTranslator.StreamAsync(options, entry.Term, LookupDirection.EnglishToChinese, timeout.Token))
            {
                builder.Append(chunk);
                if (Environment.TickCount64 - lastUiUpdateTicks >= 100)
                {
                    lastUiUpdateTicks = Environment.TickCount64;
                    _lookupWindow.ShowAiEnhanceText(builder.ToString().Trim());
                }
            }

            var text = builder.ToString().Trim();
            if (text.Length == 0)
            {
                _lookupWindow.ShowAiEnhanceError("AI 未返回内容。");
            }
            else
            {
                _lookupWindow.ShowAiEnhanceText(text);
                DiagnosticsLog.Write("PRO", $"ai enhance ok len={text.Length} in {watch.ElapsedMilliseconds} ms");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            DiagnosticsLog.Write("PRO", $"ai enhance fail: {exception.GetType().Name} {exception.Message}");
            _lookupWindow.ShowAiEnhanceError($"AI 生成失败：{exception.Message}");
        }
        finally
        {
            _lookupWindow.CompleteAiEnhance();
        }
    }

    private static void OpenWikiLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            DiagnosticsLog.Write("PRO", "wiki link opened");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            DiagnosticsLog.Write("PRO", $"open link failed: {exception.Message}");
        }
    }

    // ---- 词库页：下载与热加载 ----

    private void NavigateLibraryButton_OnClick(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        UpdateLibraryStatus();
    }

    private async void DownloadLexiconButton_OnClick(object sender, RoutedEventArgs e) =>
        await DownloadDictionaryAsync(LexiconDownloadUrl, Path.Combine(DictsDirectory, "lexicon.ttlx"),
            "中英词库", LexiconStatusText, LexiconDownloadButton, LexiconDownloadProgress, expectLexicon: true);

    private async void DownloadEedictButton_OnClick(object sender, RoutedEventArgs e) =>
        await DownloadDictionaryAsync(EedictDownloadUrl, Path.Combine(DictsDirectory, "eedict.json"),
            "专业词库", ProfessionalStatusText, ProfessionalDownloadButton, ProfessionalDownloadProgress, expectLexicon: false);

    private async Task DownloadDictionaryAsync(
        string url,
        string targetPath,
        string label,
        System.Windows.Controls.TextBlock statusText,
        System.Windows.Controls.Button button,
        System.Windows.Controls.ProgressBar progressBar,
        bool expectLexicon)
    {
        if (_downloadInFlight)
        {
            return;
        }

        _downloadInFlight = true;
        button.IsEnabled = false;
        progressBar.Visibility = Visibility.Visible;
        progressBar.Value = 0;
        statusText.Text = $"状态：正在下载{label}…";
        var temporaryPath = targetPath + ".download";
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            long received = 0;
            using (var response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = File.Create(temporaryPath);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total > 0 && received * 100.0 / total - progressBar.Value >= 1)
                    {
                        progressBar.Value = received * 100.0 / total;
                        statusText.Text = $"状态：正在下载{label}… {progressBar.Value:F0}%（{received / 1048576.0:F1}/{total / 1048576.0:F1} MB）";
                    }
                }
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            statusText.Text = $"状态：正在校验并加载{label}…";
            var (provider, lexiconStatus) = await Task.Run(LoadLocalProvider);
            var professional = await Task.Run(LoadProfessionalDictionary);
            var valid = expectLexicon ? provider is LexiconDictionaryProvider : professional is not null;
            _localProvider = provider;
            _dictionaryLookup = new DictionaryLookup(provider, new MyMemoryLookupProvider(_httpClient));
            _professional = professional;
            DiagnosticsLog.Write("LIB", $"downloaded {label} ({received} bytes) valid={valid}");
            UpdateLibraryStatus();
            UpdateHomeSummary();
            SetStatus(valid
                ? $"{label}下载完成，已立即生效。{lexiconStatus}"
                : $"{label}下载完成，但文件校验失败，请重试或检查网络。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            DiagnosticsLog.Write("LIB", $"download failed: {exception.GetType().Name} {exception.Message}");
            statusText.Text = $"状态：下载失败（{exception.Message}）";
            UpdateLibraryStatus();
            SetStatus($"{label}下载失败：{exception.Message}");
        }
        finally
        {
            button.IsEnabled = true;
            progressBar.Visibility = Visibility.Collapsed;
            _downloadInFlight = false;
        }
    }

    private void UpdateLibraryStatus()
    {
        var lexiconPath = Path.Combine(DictsDirectory, "lexicon.ttlx");
        LexiconStatusText.Text = _localProvider is LexiconDictionaryProvider lexiconProvider
            ? $"状态：已加载 {lexiconProvider.WordCount:N0} 个词条"
            : File.Exists(lexiconPath)
                ? "状态：文件已下载，重新下载以加载"
                : "状态：未下载（本地查词暂时使用演示词条）";
        LexiconDownloadButton.Content = File.Exists(lexiconPath) ? "重新下载 / 更新" : "下载中英词库（约 34MB）";

        var professionalPath = Path.Combine(DictsDirectory, "eedict.json");
        ProfessionalStatusText.Text = _professional is not null
            ? $"状态：已加载 {_professional.Count} 个专业词条"
            : File.Exists(professionalPath)
                ? "状态：文件已下载，重新下载以加载"
                : "状态：未下载（专业模式无法使用）";
        ProfessionalDownloadButton.Content = File.Exists(professionalPath) ? "重新下载 / 更新" : "下载专业词库（约 1MB）";
    }

    private void OnMousePointerPressed()
    {
        _typingBuffer.Clear();

        if (_screenshotOverlay is not null || Environment.TickCount64 < _screenshotSuppressGesturesUntilTicks)
        {
            return;
        }

        if (!_lookupWindow.IsVisible)
        {
            return;
        }

        var point = WindowsCursor.GetPosition();
        if (IsPointInsideOwnWindow(_lookupWindow, point) || IsPointInsideOwnWindow(this, point))
        {
            return;
        }

        // Clicking anywhere outside our windows: an auto selection popup closes because the
        // click cancels the selection, and the typing/screenshot popups close because the
        // user moved on to something else.
        if (_popupTrigger == PopupTrigger.SelectionAuto)
        {
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }
        else if (_popupTrigger == PopupTrigger.Typing)
        {
            ClearTypingSession(hidePopup: true);
            SetStatus("点击了其他位置，键入翻译框已关闭。");
        }
        else if (_popupTrigger == PopupTrigger.Screenshot)
        {
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }
    }

    private void StartScreenshotCapture()
    {
        if (_screenshotOverlay is not null)
        {
            return;
        }

        DiagnosticsLog.Write("SHOT", "overlay open");
        var overlay = new ScreenshotOverlayWindow();
        _screenshotOverlay = overlay;
        overlay.RegionSelected += OnScreenshotRegionSelected;
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(_screenshotOverlay, overlay))
            {
                _screenshotOverlay = null;
            }
        };
        overlay.Show();
        overlay.Activate();
    }

    private void OnScreenshotRegionSelected(Rect region)
    {
        // Convert the DIP selection to device pixels for the screen capture.
        var overlay = _screenshotOverlay;
        if (overlay is null)
        {
            return;
        }

        var topLeft = overlay.PointToScreen(region.TopLeft);
        var bottomRight = overlay.PointToScreen(region.BottomRight);
        var captureX = (int)Math.Round(topLeft.X);
        var captureY = (int)Math.Round(topLeft.Y);
        var captureWidth = (int)Math.Round(bottomRight.X - topLeft.X);
        var captureHeight = (int)Math.Round(bottomRight.Y - topLeft.Y);
        DiagnosticsLog.Write("SHOT", $"region selected {captureWidth}x{captureHeight} at ({captureX},{captureY})");
        _screenshotSuppressGesturesUntilTicks = Environment.TickCount64 + 1500;
        _ = RunScreenshotTranslationAsync(captureX, captureY, captureWidth, captureHeight);
    }

    private async Task RunScreenshotTranslationAsync(int x, int y, int width, int height)
    {
        // Let the overlay disappear from the compositor before capturing.
        await Task.Delay(150);
        _lastSelectionLookupUtc = DateTime.UtcNow;
        CancelLookup();
        _lookupCancellation = new CancellationTokenSource();
        var cancellationToken = _lookupCancellation.Token;

        SetStatus("正在识别截图文字…");
        _popupExpiresAtUtc = DateTime.MinValue;
        _popupTrigger = PopupTrigger.Screenshot;
        _popupShownAtUtc = DateTime.UtcNow;
        _lookupWindow.ShowTypingStatus("正在识别截图文字…", typingPopup: false);
        UpdatePopupAutoCloseTimer();

        DiagnosticsLog.Write("SHOT", "capture start");
        var captureStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ocr = await Task.Run(() => ScreenOcr.CaptureAsync(x, y, width, height));
        captureStopwatch.Stop();
        DiagnosticsLog.Write("SHOT", $"capture done in {captureStopwatch.ElapsedMilliseconds} ms");
        if (!ocr.Ok || string.IsNullOrWhiteSpace(ocr.Text))
        {
            DiagnosticsLog.Write("SHOT", $"ocr fail: {ocr.Message}");
            ShowPopupMessage("截图翻译", ocr.Message ?? "未识别到文字。", PopupTrigger.Screenshot);
            SetStatus(ocr.Message ?? "未识别到文字。");
            return;
        }

        var text = OcrTextCleaner.Normalize(ocr.Text);
        DiagnosticsLog.Write("SHOT", $"ocr ok len={text.Length} region={width}x{height}");
        if (text.Length == 0)
        {
            ShowPopupMessage("截图翻译", "未识别到文字。", PopupTrigger.Screenshot);
            return;
        }

        if (text.Length > DictionaryLookup.MaxSelectionQueryLength)
        {
            // Same length policy as the selection path: an oversized query would be
            // rejected as "查询内容过长" and wrongly trip the online backoff.
            text = text[..DictionaryLookup.MaxSelectionQueryLength];
        }

        var direction = OcrTextCleaner.LooksChinese(text)
            ? LookupDirection.ChineseToEnglish
            : LookupDirection.EnglishToChinese;
        // 专业词库优先（与划词同一策略）。
        if (_settings.ProfessionalModeEnabled && _professional is not null &&
            _professional.TryFind(text, out var professionalEntry))
        {
            ShowProfessionalPopup(professionalEntry, PopupTrigger.Screenshot);
            SetTrayStatus($"专业词库：「{Shorten(text, 14)}」→ {professionalEntry.Chinese}");
            return;
        }

        try
        {
            var response = await _dictionaryLookup.FindSelectionAsync(text, direction, allowOnline: false, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (response.Found)
            {
                ShowPopupEntries(response.Entries, PopupTrigger.Screenshot);
                SetStatus($"截图翻译：查到 {response.Entries.Count} 条结果。");
                SetTrayStatus($"截图翻译：「{Shorten(text, 14)}」查到 {response.Entries.Count} 条");
                return;
            }

            _lookupWindow.ShowMessage("截图翻译", $"「{Shorten(text, 60)}」\n正在翻译…");
            if (AiConfigured && await TryAiTranslationAsync(text, direction, PopupTrigger.Screenshot, cancellationToken))
            {
                SetStatus("截图翻译完成（AI）。");
                SetTrayStatus($"截图翻译：「{Shorten(text, 14)}」完成");
                return;
            }

            if (_settings.AllowOnlineLookup && DateTime.UtcNow >= _onlineBackoffUntilUtc)
            {
                response = await _dictionaryLookup.FindSelectionAsync(text, direction, allowOnline: true, cancellationToken);
                if (response.Error is not null)
                {
                    NoteOnlineFailure(response.Error);
                }

                if (response.Found)
                {
                    ShowPopupEntries(response.Entries, PopupTrigger.Screenshot);
                    SetStatus($"截图翻译：查到 {response.Entries.Count} 条结果（在线）。");
                    return;
                }
            }

            var message = response.Error ?? "未查询到译文（可在设置中配置 AI 翻译以获得整句翻译）。";
            ShowPopupMessage("截图翻译", $"「{Shorten(text, 60)}」\n{message}", PopupTrigger.Screenshot);
            SetStatus(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DispatchInput(Action action)
    {
        try
        {
            // Handlers must return control to the low-level hooks immediately; heavy UI
            // work on the hook thread is what made the mouse pointer stutter.
            Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
    }

    private void ShowTypingPad()
    {
        _popupTrigger = PopupTrigger.Typing;
        _popupShownAtUtc = DateTime.UtcNow;
        _popupExpiresAtUtc = DateTime.MinValue;
        _lookupWindow.ShowTypingStatus("it is typing now", _typingAnchor, _typingInputBounds);
        ShowRetainedPreview();
        UpdatePopupAutoCloseTimer();
    }

    private void ShowRetainedPreview()
    {
        var rows = new List<LookupWindow.RetainedRow>(_typingRetained);
        if (_typingPendingRetain is { } pending)
        {
            rows.Add(new LookupWindow.RetainedRow(pending.Word, pending.Gloss));
        }

        _lookupWindow.SetRetainedRows(rows);
    }

    private void MarkTypingResult(string token, string gloss)
    {
        ApplyPendingRetain(token);
        _typingDetailsShown = true;
        _typingLastToken = token;
        _typingLastGloss = gloss;
    }

    private void ApplyPendingRetain(string currentToken)
    {
        if (_typingPendingRetain is { } pending)
        {
            _typingPendingRetain = null;
            // The user kept extending the same token after the translation appeared; it was
            // never a finished word, so it does not belong in the retained strip.
            if (!currentToken.StartsWith(pending.Word, StringComparison.OrdinalIgnoreCase))
            {
                _typingRetained.Add(new LookupWindow.RetainedRow(pending.Word, pending.Gloss));
                while (_typingRetained.Count > MaxTypingRetainedRows)
                {
                    _typingRetained.RemoveAt(0);
                }
            }
        }

        ShowRetainedPreview();
    }

    private void ClearTypingSession(bool hidePopup)
    {
        _typingRetained.Clear();
        _typingPendingRetain = null;
        _typingLastToken = null;
        _typingLastGloss = null;
        _typingDetailsShown = false;
        if (hidePopup && _popupTrigger == PopupTrigger.Typing)
        {
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
        }

        _lookupWindow.SetRetainedRows([]);
    }

    private bool HasTypingSessionContent() =>
        _typingRetained.Count > 0 ||
        _typingPendingRetain is not null ||
        _typingDetailsShown ||
        (_popupTrigger == PopupTrigger.Typing && _lookupWindow.IsVisible);

    private bool TypingSentenceEnded(string? token, string? context, int caret)
    {
        var buffer = _typingBuffer.Length > 0 ? _typingBuffer.Snapshot() : null;
        if (!string.IsNullOrEmpty(buffer))
        {
            var lastEnder = buffer.LastIndexOfAny(TypingSentenceEnders);
            if (lastEnder >= 0)
            {
                var after = buffer[(lastEnder + 1)..].Trim();
                if (after.Length == 0 ||
                    (token is not null && string.Equals(after, token, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        // Compatibility buffer empty (disabled or just cleared): fall back to the caret text.
        if (context is not null && caret > 0 && caret <= context.Length)
        {
            var index = caret - 1;
            while (index >= 0 && context[index] == ' ')
            {
                index--;
            }

            if (index >= 0 && TypingSentenceEnders.Contains(context[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static string ConciseGloss(IReadOnlyList<DictionaryEntry> entries)
    {
        if (entries.Count > 0 && entries[0].Direction == LookupDirection.ChineseToEnglish)
        {
            return Shorten(
                string.Join("；", entries.Select(entry => entry.Query).Distinct(StringComparer.OrdinalIgnoreCase)),
                28);
        }

        foreach (var entry in entries)
        {
            foreach (var definition in entry.Definitions)
            {
                if (!string.IsNullOrWhiteSpace(definition))
                {
                    return Shorten(definition.Trim(), 28);
                }
            }
        }

        return string.Empty;
    }

    private void ShowPopupEntries(
        IReadOnlyList<DictionaryEntry> entries,
        PopupTrigger trigger,
        string? typingSourceQuery = null)
    {
        _popupTrigger = trigger;
        _popupShownAtUtc = DateTime.UtcNow;
        _popupExpiresAtUtc = DateTime.MinValue;
        _lookupWindow.ShowEntries(
            entries,
            trigger == PopupTrigger.Typing,
            trigger == PopupTrigger.Typing ? _typingAnchor : null,
            trigger == PopupTrigger.Typing ? _typingInputBounds : null,
            trigger == PopupTrigger.Typing ? typingSourceQuery : null);
        UpdatePopupAutoCloseTimer();
    }

    private void ShowPopupMessage(string title, string message, PopupTrigger trigger)
    {
        _popupTrigger = trigger;
        _popupShownAtUtc = DateTime.UtcNow;
        // Typing "not found" notices fade out on their own; selection popups keep
        // their existing lifecycle rules.
        _popupExpiresAtUtc = trigger == PopupTrigger.Typing
            ? DateTime.UtcNow.AddSeconds(6)
            : DateTime.MinValue;
        _lookupWindow.ShowMessage(
            title,
            message,
            trigger == PopupTrigger.Typing,
            trigger == PopupTrigger.Typing ? _typingAnchor : null,
            trigger == PopupTrigger.Typing ? _typingInputBounds : null);
        UpdatePopupAutoCloseTimer();
    }

    private void UpdatePopupAutoCloseTimer()
    {
        if ((_popupTrigger == PopupTrigger.SelectionHotkey && _settings.SelectionHotkeyAutoClose) ||
            _popupExpiresAtUtc != DateTime.MinValue)
        {
            _popupAutoCloseTimer.Start();
        }
        else
        {
            _popupAutoCloseTimer.Stop();
        }
    }

    private void PopupAutoCloseTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_lookupWindow.IsVisible)
        {
            _popupAutoCloseTimer.Stop();
            _popupTrigger = PopupTrigger.None;
            _popupExpiresAtUtc = DateTime.MinValue;
            return;
        }

        if (_popupExpiresAtUtc != DateTime.MinValue && DateTime.UtcNow >= _popupExpiresAtUtc)
        {
            _lookupWindow.Hide();
            _popupTrigger = PopupTrigger.None;
            _popupExpiresAtUtc = DateTime.MinValue;
            _popupAutoCloseTimer.Stop();
            return;
        }

        var chordHeld = _selectionHotkey?.IsChordHeld() == true;
        if (!chordHeld && DateTime.UtcNow - _popupShownAtUtc >= TimeSpan.FromSeconds(3))
        {
            _lookupWindow.Hide();
            _popupAutoCloseTimer.Stop();
            _popupTrigger = PopupTrigger.None;
        }
    }

    private void HotkeyAutoCloseCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.SelectionHotkeyAutoClose = HotkeyAutoCloseCheckBox.IsChecked == true;
        UpdatePopupAutoCloseTimer();
        SaveSettingsOrReport();
    }

    private static readonly (string Name, string BaseUrl, string Model)[] AiPresets =
    [
        ("DeepSeek（推荐）", "https://api.deepseek.com", "deepseek-chat"),
        ("通义千问（阿里云）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("Kimi（月之暗面）", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("豆包（火山方舟，模型填 Endpoint ID）", "https://ark.cn-beijing.volces.com/api/v3", ""),
        ("自定义", "", "")
    ];

    private bool AiConfigured =>
        _settings.AiEnabled &&
        !string.IsNullOrWhiteSpace(_settings.AiApiKey) &&
        !string.IsNullOrWhiteSpace(_settings.AiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settings.AiModel);

    private void AiEnabledCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AiEnabled = AiEnabledCheckBox.IsChecked == true;
        DiagnosticsLog.Write("SETTINGS", $"aiEnabled={_settings.AiEnabled} configured={AiConfigured}");
        UpdateHomeSummary();
        SaveSettingsOrReport();
    }

    private void AiPresetComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || AiPresetComboBox.SelectedIndex < 0)
        {
            return;
        }

        var preset = AiPresets[AiPresetComboBox.SelectedIndex];
        if (preset.BaseUrl.Length == 0)
        {
            return;
        }

        AiBaseUrlTextBox.Text = preset.BaseUrl;
        if (preset.Model.Length > 0)
        {
            AiModelTextBox.Text = preset.Model;
        }

        PersistAiFields();
    }

    private void AiField_OnLostFocus(object sender, RoutedEventArgs e) => PersistAiFields();

    private void AiStreamingCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AiStreaming = AiStreamingCheckBox.IsChecked == true;
        SaveSettingsOrReport();
    }

    private void AiContextCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AiContextEnhance = AiContextCheckBox.IsChecked == true;
        DiagnosticsLog.Write("SETTINGS", $"aiContextEnhance={_settings.AiContextEnhance} configured={AiConfigured}");
        SaveSettingsOrReport();
        if (_settings.AiContextEnhance && !AiConfigured)
        {
            SetStatus("已开启 AI 增强翻译，但 AI 尚未配置完整：请在下方填写接口地址、模型与 API Key 并勾选「启用 AI 翻译」。");
        }
        else if (_settings.AiContextEnhance)
        {
            SetStatus("AI 增强翻译已开启：划选单词或短语时，将结合上下文推断词义并给出情境化译文。");
        }
    }

    private void PersistAiFields()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AiBaseUrl = AiBaseUrlTextBox.Text.Trim();
        _settings.AiModel = AiModelTextBox.Text.Trim();
        _settings.AiApiKey = AiApiKeyBox.Password.Trim();
        UpdateHomeSummary();
        SaveSettingsOrReport();
    }

    private async void AiTestButton_OnClick(object sender, RoutedEventArgs e)
    {
        PersistAiFields();
        if (!AiConfigured)
        {
            SetStatus("AI 未配置完整：请填写接口地址、模型与 API Key，并勾选「启用 AI 翻译」。");
            return;
        }

        SetStatus("正在测试 AI 连接…");
        var options = new AiTranslatorOptions(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel);
        var builder = new System.Text.StringBuilder();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await foreach (var chunk in _aiTranslator.StreamAsync(options, "hello", LookupDirection.EnglishToChinese, timeout.Token))
            {
                builder.Append(chunk);
            }

            var text = builder.ToString().Trim();
            DiagnosticsLog.Write("AI", $"test ok len={text.Length}");
            SetStatus(text.Length > 0
                ? $"AI 连接成功！示例翻译：hello → {Shorten(text, 60)}"
                : "AI 已连接，但没有返回内容（请检查模型名）。");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            DiagnosticsLog.Write("AI", $"test fail: {exception.GetType().Name} {exception.Message}");
            SetStatus($"AI 连接失败：{exception.Message}");
        }
    }

    private void ShowPopupStreaming(string title, string text, PopupTrigger trigger)
    {
        _popupTrigger = trigger;
        _popupExpiresAtUtc = DateTime.MinValue;
        _lookupWindow.ShowStreamingText(
            title,
            text,
            trigger == PopupTrigger.Typing ? _typingAnchor : null,
            trigger == PopupTrigger.Typing ? _typingInputBounds : null);
        UpdatePopupAutoCloseTimer();
    }

    private async Task<bool> TryAiTranslationAsync(
        string query,
        LookupDirection direction,
        PopupTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!AiConfigured)
        {
            return false;
        }

        if (_translationCache.TryGet(query, direction, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            DiagnosticsLog.Write("AI", $"cache hit len={cached.Length}");
            ShowPopupEntries([new DictionaryEntry(query, direction, [cached], "AI 翻译（缓存）")], trigger);
            return true;
        }

        var options = new AiTranslatorOptions(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel);
        var builder = new System.Text.StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ShowPopupStreaming("AI 翻译", "正在翻译…", trigger);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var firstChunk = true;
            var lastUiUpdateTicks = Environment.TickCount64;
            await foreach (var chunk in _aiTranslator.StreamAsync(options, query, direction, timeout.Token))
            {
                if (firstChunk)
                {
                    firstChunk = false;
                    DiagnosticsLog.Write("AI", $"first chunk after {watch.ElapsedMilliseconds} ms");
                }

                builder.Append(chunk);
                if (_settings.AiStreaming && Environment.TickCount64 - lastUiUpdateTicks >= 100)
                {
                    // Throttled: updating the popup on every stream chunk stalls the UI
                    // thread often enough to delay the low-level input hooks (mouse stutter).
                    lastUiUpdateTicks = Environment.TickCount64;
                    _lookupWindow.UpdateStreamingText(builder.ToString());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Anything except our own cancellation (bad URL, proxy errors, malformed
            // responses) must surface as a failed translation, not a crash.
            DiagnosticsLog.Write("AI", $"fail after {watch.ElapsedMilliseconds} ms: {exception.GetType().Name} {exception.Message}");
            return false;
        }

        var text = builder.ToString().Trim().Trim('"', '\'', '“', '”', '「', '」');
        if (text.Length == 0)
        {
            DiagnosticsLog.Write("AI", "empty result");
            return false;
        }

        _translationCache.Set(query, direction, text);
        DiagnosticsLog.Write("AI", $"ok len={text.Length} in {watch.ElapsedMilliseconds} ms");
        ShowPopupEntries([new DictionaryEntry(query, direction, [text], "AI 翻译")], trigger);
        return true;
    }

    private async Task RunContextEnhanceAsync(string query, LookupDirection direction, CancellationToken cancellationToken)
    {
        var options = new AiTranslatorOptions(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel);
        string? context = null;
        try
        {
            var contextTask = Task.Run(() => FocusedTextReader.ReadContextAroundSelection(query), cancellationToken);
            if (await Task.WhenAny(contextTask, Task.Delay(1200, cancellationToken)) == contextTask)
            {
                var contextResult = await contextTask;
                context = contextResult.Ok ? contextResult.Context : null;
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            else
            {
                // Busy app: continue without context instead of hanging the enhancement.
                _ = contextTask.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        if (!_settings.SelectionLookupEnabled || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        DiagnosticsLog.Write("AI", $"enhance start ctxLen={context?.Length ?? 0} qlen={query.Length}");
        _lookupWindow.SetAiSection("AI 增强翻译", "正在结合上下文分析…");
        _popupShownAtUtc = DateTime.UtcNow;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var builder = new System.Text.StringBuilder();
        var firstChunk = true;
        var lastUiUpdateTicks = Environment.TickCount64;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await foreach (var chunk in _aiTranslator.StreamWithPromptAsync(
                               options,
                               ContextEnhancePrompt.BuildSystem(direction),
                               ContextEnhancePrompt.BuildUser(query, context, direction),
                               maxTokens: 320,
                               temperature: 0.3,
                               timeout.Token))
            {
                if (firstChunk)
                {
                    firstChunk = false;
                    DiagnosticsLog.Write("AI", $"enhance first chunk after {watch.ElapsedMilliseconds} ms");
                }

                builder.Append(chunk);
                // Keep the popup alive while the AI works even without live streaming:
                // otherwise a slow response is silently swallowed by the auto-close.
                _popupShownAtUtc = DateTime.UtcNow;
                if (_settings.AiStreaming && Environment.TickCount64 - lastUiUpdateTicks >= 100)
                {
                    lastUiUpdateTicks = Environment.TickCount64;
                    _lookupWindow.SetAiSection("AI 增强翻译", builder.ToString().Trim());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            DiagnosticsLog.Write("AI", $"enhance fail after {watch.ElapsedMilliseconds} ms: {exception.GetType().Name} {exception.Message}");
            _lookupWindow.SetAiSection("AI 增强翻译", $"AI 增强失败：{exception.Message}", isError: true);
            _popupShownAtUtc = DateTime.UtcNow;
            return;
        }

        var text = builder.ToString().Trim().Trim('"', '\'', '“', '”', '「', '」');
        if (text.Length == 0)
        {
            DiagnosticsLog.Write("AI", "enhance empty result");
            _lookupWindow.SetAiSection("AI 增强翻译", "AI 未返回内容（请检查模型名）。", isError: true);
            _popupShownAtUtc = DateTime.UtcNow;
            return;
        }

        DiagnosticsLog.Write("AI", $"enhance ok len={text.Length} in {watch.ElapsedMilliseconds} ms");
        _lookupWindow.SetAiSection("AI 增强翻译", text);
        _popupShownAtUtc = DateTime.UtcNow;
        SetStatus("AI 增强翻译完成（语境义与情境翻译见弹窗）。");
        SetTrayStatus($"划词：「{Shorten(query, 16)}」AI 增强完成");
    }

    private void OnDragSelectCompleted(Point start, Point end, long hookTicks)
    {
        var dispatchLagMs = Environment.TickCount64 - hookTicks;
        if (dispatchLagMs >= 15)
        {
            DiagnosticsLog.Write("SEL", $"dispatch lag {dispatchLagMs} ms");
        }

        // The screenshot overlay swallows gestures while region selection is active
        // (and briefly after, since the overlay window closes before the hook callback
        // is dispatched to us).
        if (_screenshotOverlay is not null || Environment.TickCount64 < _screenshotSuppressGesturesUntilTicks)
        {
            return;
        }

        if (!_settings.SelectionLookupEnabled)
        {
            if (TraceSelection)
            {
                DiagnosticsLog.Write("SEL", "gesture ignored: module off");
            }

            return;
        }

        // Screen-capture overlays (Snipaste etc.) are not text selections: running the
        // Ctrl+C / Ctrl+Insert read into them interferes with the capture tool's own
        // clipboard hand-off (a late injected key can land in the app that regains
        // focus and overwrite the freshly copied screenshot). Leave them alone.
        var foregroundProcess = GetForegroundProcessName();
        if (IsScreenCaptureTool(foregroundProcess))
        {
            DiagnosticsLog.Write("SEL", $"skip: gesture in capture tool ({foregroundProcess})");
            return;
        }

        // Ignore gestures that begin on our own windows (dragging the popup or the
        // main window); a selection that merely ends over the popup still counts.
        if (IsPointInsideOwnWindow(_lookupWindow, start) || IsPointInsideOwnWindow(this, start))
        {
            DiagnosticsLog.Write("SEL", $"skip: gesture starts on our window ({start.X:F0},{start.Y:F0})");
            return;
        }

        if (TraceSelection)
        {
            DiagnosticsLog.Write("SEL",
                $"gesture start=({start.X:F0},{start.Y:F0}) end=({end.X:F0},{end.Y:F0}) auto={_settings.SelectionLookupAuto}");
        }

        if (_settings.SelectionLookupAuto)
        {
            _ = SelectionLookupAsync(end, triggeredByHotkey: false);
            return;
        }

        if (_settings.SelectionHotkeyEnabled)
        {
            SetStatus($"已选中文本：按 {_settings.SelectionHotkey} 划词翻译。");
        }
    }

    private static bool IsScreenCaptureTool(string? processName) =>
        processName is not null &&
        (processName.Equals("Snipaste", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("PixPin", StringComparison.OrdinalIgnoreCase));

    private async Task SelectionLookupAsync(Point? point, bool triggeredByHotkey)
    {
        if (!IsSelectionModeEnabled(triggeredByHotkey))
        {
            if (triggeredByHotkey)
            {
                ShowPopupMessage("快捷键划词",
                    $"快捷键划词已关闭，无法翻译。\n按 {_settings.SelectionToggleHotkey} 可重新开启。",
                    PopupTrigger.SelectionHotkey);
                DiagnosticsLog.Write("SEL", "trigger ignored: hotkey mode off");
            }

            return;
        }

        if (triggeredByHotkey && IsForegroundWindowOurProcess())
        {
            DiagnosticsLog.Write("SEL", "skip: foreground is our window");
            ShowPopupMessage("划词翻译", "请先在其他应用中用鼠标选中文本，再按快捷键。", PopupTrigger.SelectionHotkey);
            return;
        }

        if (DateTime.UtcNow - _lastSelectionLookupUtc < TimeSpan.FromMilliseconds(250))
        {
            if (TraceSelection)
            {
                DiagnosticsLog.Write("SEL", "gesture ignored: debounce (250 ms)");
            }

            return;
        }

        _lastSelectionLookupUtc = DateTime.UtcNow;

        DiagnosticsLog.Write("SEL", $"trigger={(triggeredByHotkey ? "hotkey" : "auto")} app={GetForegroundProcessName()}");

        if (triggeredByHotkey)
        {
            ShowPopupMessage("划词翻译", "正在读取选中的文本…", PopupTrigger.None);
        }

        CancelLookup();
        _lookupCancellation = new CancellationTokenSource();
        var cancellationToken = _lookupCancellation.Token;
        SetStatus("正在读取选中的文本…");

        try
        {
            var readResult = await SelectionReader.ReadAsync();
            if (!readResult.Success || string.IsNullOrWhiteSpace(readResult.Text))
            {
                var message = readResult.Message ?? "未读取到选中的文本。";
                SetStatus(message);
                SetTrayStatus($"划词：未读取到选中文本（{GetForegroundProcessName()}）");
                if (triggeredByHotkey)
                {
                    ShowPopupMessage("划词翻译", message, PopupTrigger.SelectionHotkey);
                }

                return;
            }

            var text = NormalizeSelection(readResult.Text);
            if (text.Length == 0)
            {
                SetStatus("选中的文本为空。");
                return;
            }

            var direction = GlossTokenizer.ContainsCjk(text)
                ? LookupDirection.ChineseToEnglish
                : LookupDirection.EnglishToChinese;
            var popupTrigger = triggeredByHotkey ? PopupTrigger.SelectionHotkey : PopupTrigger.SelectionAuto;
            // 专业词库优先：命中专业词条时直接显示中英对照/解释/百科链接面板。
            if (_settings.ProfessionalModeEnabled && _professional is not null &&
                _professional.TryFind(text, out var professionalEntry))
            {
                ShowProfessionalPopup(professionalEntry, popupTrigger);
                SetTrayStatus($"专业词库：「{Shorten(text, 16)}」→ {professionalEntry.Chinese}");
                return;
            }

            var lookupStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await _dictionaryLookup.FindSelectionAsync(text, direction, allowOnline: false, cancellationToken);
            lookupStopwatch.Stop();
            if (lookupStopwatch.ElapsedMilliseconds >= 20)
            {
                DiagnosticsLog.Write("SEL", $"lookup {lookupStopwatch.ElapsedMilliseconds} ms len={text.Length}");
            }

            if (!IsSelectionModeEnabled(triggeredByHotkey) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // AI context enhancement takes over the fallback paths for single words and
            // short phrases: instead of a context-free translation, the AI receives the
            // text surrounding the selection and infers the meaning in context.
            var enhanceEligible = _settings.AiContextEnhance && AiConfigured && ContextEnhancePrompt.Qualifies(text);
            if (enhanceEligible)
            {
                if (response.Found)
                {
                    DiagnosticsLog.Write("SEL",
                        $"lookup len={text.Length} entries={response.Entries.Count} online=False error=none");
                    ShowPopupEntries(response.Entries, popupTrigger);
                    SetStatus($"划词查到 {response.Entries.Count} 条结果，AI 增强分析中…");
                    SetTrayStatus($"划词：「{Shorten(text, 16)}」查到 {response.Entries.Count} 条（AI 增强中）");
                }
                else
                {
                    ShowPopupMessage("划词翻译",
                        $"「{Shorten(text, 40)}」\n本地未收录，正在用 AI 结合上下文分析…",
                        popupTrigger);
                    SetStatus("本地未收录，正在用 AI 结合上下文分析…");
                }

                await RunContextEnhanceAsync(text, direction, cancellationToken);
                return;
            }

            if (!response.Found && AiConfigured)
            {
                SetStatus("本地未收录，正在用 AI 翻译…");
                if (await TryAiTranslationAsync(text, direction, popupTrigger, cancellationToken))
                {
                    SetStatus("AI 翻译完成。");
                    SetTrayStatus($"划词：已翻译「{Shorten(text, 14)}」");
                    return;
                }
            }

            if (!response.Found && _settings.AllowOnlineLookup && DateTime.UtcNow >= _onlineBackoffUntilUtc)
            {
                response = await _dictionaryLookup.FindSelectionAsync(text, direction, allowOnline: true, cancellationToken);
                if (response.Error is not null)
                {
                    NoteOnlineFailure(response.Error);
                }

                if (!IsSelectionModeEnabled(triggeredByHotkey) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            else if (!response.Found && _settings.AllowOnlineLookup)
            {
                response = new([], true, "在线翻译暂不可用（近期失败过，稍后自动重试）。");
            }

            DiagnosticsLog.Write("SEL",
                $"lookup len={text.Length} entries={response.Entries.Count} online={response.IsOnline} error={response.Error ?? "none"}");

            if (response.Found)
            {
                ShowPopupEntries(response.Entries, popupTrigger);
                SetStatus($"划词查到 {response.Entries.Count} 条结果。");
                SetTrayStatus($"划词：「{Shorten(text, 16)}」查到 {response.Entries.Count} 条");
            }
            else
            {
                var message = response.Error ?? "未查询到选中文本的译文（可在设置中开启在线补查或 AI 翻译）。";
                SetStatus(message);
                SetTrayStatus($"划词：「{Shorten(text, 16)}」未查到译文");
                ShowPopupMessage("划词翻译", $"「{Shorten(text, 40)}」\n{message}", popupTrigger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string GetForegroundProcessName()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return "unknown";
            }

            GetWindowThreadProcessId(foreground, out var processId);
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string NormalizeSelection(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length > DictionaryLookup.MaxSelectionQueryLength
            ? collapsed[..DictionaryLookup.MaxSelectionQueryLength]
            : collapsed;
    }

    private bool IsForegroundWindowOurProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private bool IsPointInsideOwnWindow(Window window, Point point)
    {
        if (!window.IsVisible)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return false;
        }

        if (point.X < rect.Left || point.X > rect.Right || point.Y < rect.Top || point.Y > rect.Bottom)
        {
            return false;
        }

        // The rectangle alone is not enough: our window may merely be *behind* the app the
        // user is working in. Only claim the point when a window of this process is actually
        // on top there, otherwise gestures made in the app above us would be silently dropped.
        var top = WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        if (top == IntPtr.Zero)
        {
            return false;
        }

        var root = GetAncestor(top, GaRoot);
        GetWindowThreadProcessId(root == IntPtr.Zero ? top : root, out var processId);
        return processId == Environment.ProcessId;
    }

    private void OnLookupWindowSizeAdjusted(double width, double maxContentHeight)
    {
        _settings.PopupWidth = width;
        _settings.PopupMaxHeight = maxContentHeight;
        PopupWidthTextBox.Text = width.ToString("F0");
        PopupHeightTextBox.Text = maxContentHeight.ToString("F0");
        SaveSettingsOrReport();
    }

    private void FontComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || FontComboBox.SelectedItem is not FontFamily family)
        {
            return;
        }

        _settings.PopupFontFamily = family.Source;
        ApplyPopupAppearance();
        SaveSettingsOrReport();
    }

    private void FontSizeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.PopupFontSize = FontSizeSlider.Value;
        UpdateAppearanceLabels();
        ApplyPopupAppearance();
        ScheduleAppearanceSave();
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.PopupOpacity = OpacitySlider.Value;
        UpdateAppearanceLabels();
        ApplyPopupAppearance();
        ScheduleAppearanceSave();
    }

    private void ApplyPopupLayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PopupWidthTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var width) ||
            !double.TryParse(PopupHeightTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var height) ||
            !double.IsFinite(width) || !double.IsFinite(height))
        {
            SetStatus("浮窗尺寸无效，请输入数字。");
            return;
        }

        _settings.PopupWidth = Math.Clamp(width, 280, 1200);
        _settings.PopupMaxHeight = Math.Clamp(height, 40, 1000);
        PopupWidthTextBox.Text = _settings.PopupWidth.ToString("F0");
        PopupHeightTextBox.Text = _settings.PopupMaxHeight.ToString("F0");
        ApplyPopupAppearance();
        SaveSettingsOrReport();
    }

    private void ApplyPopupColorsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.PopupBackgroundColor = BackgroundColorEditor.SelectedColor.ToString();
        _settings.PopupTextColor = TextColorEditor.SelectedColor.ToString();
        _appearanceSaveTimer.Stop();
        ApplyPopupAppearance();
        var saved = SaveSettingsOrReport();
        SetStatus(saved ? "浮窗配色已应用。" : "配色已预览，但设置保存失败：本次配色仅在当前运行内有效。");
    }

    private void OnBackgroundColorChanged(object? sender, Color color)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.PopupBackgroundColor = color.ToString();
        ApplyPopupAppearance();
        ScheduleAppearanceSave();
    }

    private void OnTextColorChanged(object? sender, Color color)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.PopupTextColor = color.ToString();
        ApplyPopupAppearance();
        ScheduleAppearanceSave();
    }

    // Coalesces frequent appearance changes (slider drags) into a single settings write;
    // OnClosed flushes any pending change so nothing is lost when the app exits quickly.
    private void ScheduleAppearanceSave()
    {
        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Start();
    }

    private static bool TryParsePopupColor(string value, out Color color)
    {
        color = default;
        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is not Color parsed)
            {
                return false;
            }

            color = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    private void UpdateAppearanceLabels()
    {
        FontSizeValueText.Text = $"{FontSizeSlider.Value:F0}";
        OpacityValueText.Text = $"{OpacitySlider.Value * 100:F0}%";
    }

    private void ApplyPopupAppearance()
    {
        _lookupWindow.ApplyAppearance(
            _settings.PopupFontFamily,
            _settings.PopupFontSize,
            _settings.PopupOpacity,
            _settings.PopupBackgroundColor,
            _settings.PopupTextColor);
        _lookupWindow.ApplyLayout(_settings.PopupWidth, _settings.PopupMaxHeight);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var wordFamily = new FontFamily($"{_settings.PopupFontFamily}, Microsoft YaHei UI, Segoe UI");
        PreviewWordTextBlock.FontFamily = wordFamily;
        PreviewWordTextBlock.FontSize = _settings.PopupFontSize + 5;
        PreviewGlossTextBlock.FontFamily = wordFamily;
        PreviewGlossTextBlock.FontSize = _settings.PopupFontSize + 2;
        PreviewPhoneticTextBlock.FontFamily = new FontFamily("Segoe UI");
        PreviewPhoneticTextBlock.FontSize = Math.Max(10, _settings.PopupFontSize - 1);
        if (TryParsePopupColor(_settings.PopupBackgroundColor, out var background))
        {
            PopupPreviewBorder.Background = new SolidColorBrush(background);
        }

        if (TryParsePopupColor(_settings.PopupTextColor, out var text))
        {
            PreviewGlossTextBlock.Foreground = new SolidColorBrush(text);
        }
    }

    private IntPtr WindowProcedure(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            var hotkeyId = wParam.ToInt32();
            if (hotkeyId == TypingToggleHotkeyId)
            {
                SetTypingEnabled(!_settings.TypingLookupEnabled, announce: true);
                DiagnosticsLog.Write("HOTKEY", $"typing toggle -> {_settings.TypingLookupEnabled}");
                handled = true;
            }
            else if (hotkeyId == SelectionTriggerHotkeyId)
            {
                _ = SelectionLookupAsync(point: null, triggeredByHotkey: true);
                handled = true;
            }
            else if (hotkeyId == SelectionToggleHotkeyId)
            {
                SetSelectionEnabled(!_settings.SelectionHotkeyEnabled, announce: true);
                DiagnosticsLog.Write("HOTKEY", $"selection toggle -> {_settings.SelectionHotkeyEnabled}");
                handled = true;
            }
            else if (hotkeyId == SelectionAutoToggleHotkeyId)
            {
                SetSelectionAutoEnabled(!_settings.SelectionLookupAuto, announce: true);
                DiagnosticsLog.Write("HOTKEY", $"selectionAuto toggle -> {_settings.SelectionLookupAuto}");
                handled = true;
            }
            else if (hotkeyId == ScreenshotHotkeyId)
            {
                StartScreenshotCapture();
                handled = true;
            }
            else if (hotkeyId == ProfessionalHotkeyId)
            {
                SetProfessionalModeEnabled(!_settings.ProfessionalModeEnabled, announce: true);
                DiagnosticsLog.Write("HOTKEY", $"professional toggle -> {_settings.ProfessionalModeEnabled}");
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private bool SaveSettingsOrReport()
    {
        var saved = true;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (IOException exception)
        {
            saved = false;
            SetStatus($"保存设置失败：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            saved = false;
            SetStatus($"保存设置失败：{exception.Message}");
        }
        catch (SecurityException exception)
        {
            saved = false;
            SetStatus($"保存设置失败：{exception.Message}");
        }
        catch (Exception exception)
        {
            // Anything else (e.g. a value that cannot be serialized) must not crash the app.
            saved = false;
            DiagnosticsLog.Write("SET", $"save failed: {exception.GetType().Name} {exception.Message}");
            SetStatus($"保存设置失败：{exception.Message}");
        }

        // Settings interactions can stall the UI thread long enough for Windows to
        // silently drop the low-level hooks; refresh them so selection keeps working
        // without restarting the app.
        RefreshMonitors(force: false);
        return saved;
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;

    private void SetTrayStatus(string message)
    {
        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            var limit = message.Length > 63 ? 62 : message.Length;
            if (limit < message.Length && limit > 0 && char.IsHighSurrogate(message[limit - 1]))
            {
                limit--;
            }

            _trayIcon.Text = limit == message.Length ? message : message[..limit] + "…";
        }
        catch (ArgumentException)
        {
        }
    }

    private void CancelLookup()
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = null;
    }

    private void SetCheckboxWithoutEvent(System.Windows.Controls.CheckBox checkBox, bool value)
    {
        var previousLoadingState = _loadingSettings;
        _loadingSettings = true;
        checkBox.IsChecked = value;
        _loadingSettings = previousLoadingState;
    }

    protected override void OnClosed(EventArgs e)
    {
        _inputTimer.Stop();
        _typingFallbackTimer.Stop();
        _popupAutoCloseTimer.Stop();
        _hookWatchdogTimer.Stop();
        var appearanceSavePending = _appearanceSaveTimer.IsEnabled;
        _appearanceSaveTimer.Stop();
        if (appearanceSavePending)
        {
            // Flush an in-flight appearance change so a quick exit cannot lose it.
            SaveSettingsOrReport();
        }

        CancelLookup();
        _inputMonitor.InputActivity -= InputMonitor_OnInputActivity;
        _inputMonitor.CharacterTyped -= OnCharacterTyped;
        _inputMonitor.BackspaceTyped -= OnBackspaceTyped;
        try
        {
            _inputMonitor.Dispose();
        }
        catch (Win32Exception)
        {
        }

        try
        {
            _mouseMonitor.Dispose();
        }
        catch (Win32Exception)
        {
        }

        try
        {
            _typingHotkey?.Unregister();
            _selectionHotkey?.Unregister();
            _selectionToggleHotkey?.Unregister();
            _selectionAutoToggleHotkey?.Unregister();
            _screenshotHotkey?.Unregister();
        }
        catch (Win32Exception)
        {
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _windowSource?.RemoveHook(WindowProcedure);
        _lookupWindow.Close();
        _httpClient.Dispose();
        _aiHttpClient.Dispose();
        _downloadClient.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private static void TryEnableDarkTitleBar(IntPtr handle)
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        const int DwmwaUseImmersiveDarkModeLegacy = 19;
        var enabled = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
