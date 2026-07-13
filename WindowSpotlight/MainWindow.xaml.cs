using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowSpotlight;

public partial class MainWindow : Window
{
    private readonly WindowPlatform _platform = new();
    private readonly SettingsService _settingsService = new();
    private readonly SpotlightSession _session;
    private readonly DispatcherTimer _pickerTimer;
    private IReadOnlyList<ExternalWindowInfo> _windows = [];
    private IReadOnlyList<DisplayMonitorInfo> _monitors = [];
    private DisplayMonitorInfo? _selectedMonitor;
    private PersistedSettings _settings = new();
    private PickerHighlightWindow? _pickerHighlight;
    private ExternalWindowInfo? _pickerCandidate;
    private bool _isPicking;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _session = new SpotlightSession(_platform, Dispatcher);
        _session.StatusChanged += OnSessionStatusChanged;
        _pickerTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _pickerTimer.Tick += OnPickerTimerTick;
    }

    internal void RestoreTargetWindow()
    {
        try
        {
            _session.Stop();
        }
        catch
        {
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        ApplySettingsToControls();
        RefreshWindows();
        RefreshMonitors();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _isInitialized = true;
        UpdateConfigurationAvailability();
        ShowStatus("対象ウィンドウとディスプレイを選択してください。", SessionStatusKind.Neutral);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        StopPicker(true);
        SaveSettings();
        _session.StatusChanged -= OnSessionStatusChanged;
        _session.Dispose();
        _pickerTimer.Tick -= OnPickerTimerTick;
    }

    private void ApplySettingsToControls()
    {
        PercentageSlider.Value = _settings.FitPercentage;
        ExactWidthTextBox.Text = _settings.ExactWidth.ToString();
        ExactHeightTextBox.Text = _settings.ExactHeight.ToString();
        RemoveTitleBarToggle.IsOn = _settings.RemoveTitleBar;
        switch (_settings.SizeMode)
        {
            case SizeMode.FitPercentage:
                PercentageRadio.IsChecked = true;
                break;
            case SizeMode.ExactPixels:
                ExactPixelsRadio.IsChecked = true;
                break;
            default:
                UnchangedRadio.IsChecked = true;
                break;
        }

        PercentageText.Text = $"{_settings.FitPercentage}%";
    }

    private void RefreshWindows(nint preferredHandle = default)
    {
        var previousHandle = preferredHandle != 0
            ? preferredHandle
            : (WindowComboBox.SelectedItem as ExternalWindowInfo)?.Handle ?? 0;
        _windows = _platform.EnumerateWindows();
        WindowComboBox.ItemsSource = _windows;
        WindowComboBox.SelectedItem = _windows.FirstOrDefault(window => window.Handle == previousHandle);
        UpdateConfigurationAvailability();
    }

    private void RefreshMonitors()
    {
        var previousDeviceId = _selectedMonitor?.DeviceId ?? _settings.MonitorDeviceId;
        _monitors = _platform.EnumerateMonitors();
        if (_session.IsActive && previousDeviceId is not null &&
            !_monitors.Any(monitor => string.Equals(monitor.DeviceId, previousDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            _session.Stop();
            ShowStatus("使用中のディスプレイが切断されたため停止しました。", SessionStatusKind.Warning);
        }

        _selectedMonitor = _monitors.FirstOrDefault(monitor =>
                               string.Equals(monitor.DeviceId, previousDeviceId, StringComparison.OrdinalIgnoreCase))
                           ?? _monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                           ?? _monitors.FirstOrDefault();
        DrawMonitorPreview();
        UpdateConfigurationAvailability();
    }

    private void DrawMonitorPreview()
    {
        MonitorCanvas.Children.Clear();
        if (MonitorCanvas.ActualWidth <= 0 || MonitorCanvas.ActualHeight <= 0)
        {
            return;
        }

        var layouts = GeometryCalculator.CalculateMonitorPreview(
            _monitors,
            MonitorCanvas.ActualWidth,
            MonitorCanvas.ActualHeight,
            12);
        foreach (var monitor in _monitors)
        {
            if (!layouts.TryGetValue(monitor.DeviceId, out var rect))
            {
                continue;
            }

            var selected = _selectedMonitor?.DeviceId.Equals(monitor.DeviceId, StringComparison.OrdinalIgnoreCase) == true;
            var button = new Button
            {
                Tag = monitor,
                Width = rect.Width,
                Height = rect.Height,
                Padding = new Thickness(5),
                BorderThickness = new Thickness(selected ? 3 : 1),
                BorderBrush = selected
                    ? TryFindResource("SystemControlHighlightAccentBrush") as Brush ?? Brushes.DodgerBlue
                    : TryFindResource("SystemControlForegroundBaseMediumLowBrush") as Brush ?? Brushes.Gray,
                Background = TryFindResource("SystemControlBackgroundAltHighBrush") as Brush ?? Brushes.DimGray,
                ToolTip = $"{monitor.Name}\n{monitor.Description}\n位置: {monitor.Bounds.Left}, {monitor.Bounds.Top}"
            };
            button.Click += OnMonitorButtonClick;
            button.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = monitor.Name,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = monitor.Description,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            Canvas.SetLeft(button, rect.Left);
            Canvas.SetTop(button, rect.Top);
            MonitorCanvas.Children.Add(button);
        }
    }

    private void OnMonitorButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DisplayMonitorInfo monitor })
        {
            _selectedMonitor = monitor;
            _settings.MonitorDeviceId = monitor.DeviceId;
            DrawMonitorPreview();
            UpdateConfigurationAvailability();
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (WindowComboBox.SelectedItem is not ExternalWindowInfo target || _selectedMonitor is null)
        {
            ShowStatus("対象ウィンドウとディスプレイを選択してください。", SessionStatusKind.Warning);
            return;
        }

        if (!TryReadOptions(out var options, out var validationMessage))
        {
            ShowStatus(validationMessage!, SessionStatusKind.Warning);
            return;
        }

        try
        {
            SaveSettings();
            _session.Start(target, _selectedMonitor, options!);
            SetSessionControls(true);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, SessionStatusKind.Error);
            RefreshWindows();
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.Stop();
        }
        catch (Exception exception)
        {
            ShowStatus($"復元中にエラーが発生しました: {exception.Message}", SessionStatusKind.Error);
        }
        finally
        {
            SetSessionControls(false);
            RefreshWindows();
        }
    }

    private bool TryReadOptions(out SpotlightOptions? options, out string? error)
    {
        options = null;
        error = null;
        var mode = PercentageRadio.IsChecked == true
            ? SizeMode.FitPercentage
            : ExactPixelsRadio.IsChecked == true
                ? SizeMode.ExactPixels
                : SizeMode.Unchanged;
        var widthIsValid = int.TryParse(ExactWidthTextBox.Text, out var width) && width > 0;
        var heightIsValid = int.TryParse(ExactHeightTextBox.Text, out var height) && height > 0;
        if (mode == SizeMode.ExactPixels && (!widthIsValid || !heightIsValid))
        {
            error = "ピクセルサイズは1以上の整数で入力してください。";
            return false;
        }

        width = widthIsValid ? width : _settings.ExactWidth;
        height = heightIsValid ? height : _settings.ExactHeight;

        if (_selectedMonitor is not null && mode == SizeMode.ExactPixels &&
            (width > _selectedMonitor.Bounds.Width || height > _selectedMonitor.Bounds.Height))
        {
            error = $"ピクセルサイズは選択モニターの {_selectedMonitor.Bounds.Width} × {_selectedMonitor.Bounds.Height}px 以内にしてください。";
            return false;
        }

        options = new SpotlightOptions(
            mode,
            (int)PercentageSlider.Value,
            width,
            height,
            RemoveTitleBarToggle.IsOn);
        return true;
    }

    private void SaveSettings()
    {
        if (!TryReadOptions(out var options, out _))
        {
            return;
        }

        _settings.MonitorDeviceId = _selectedMonitor?.DeviceId;
        _settings.SizeMode = options!.SizeMode;
        _settings.FitPercentage = options.FitPercentage;
        _settings.ExactWidth = options.ExactWidth;
        _settings.ExactHeight = options.ExactHeight;
        _settings.RemoveTitleBar = options.RemoveTitleBar;
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("設定を保存できませんでした。", SessionStatusKind.Warning);
        }
    }

    private void SetSessionControls(bool active)
    {
        ConfigurationPanel.IsEnabled = !active;
        StartButton.IsEnabled = !active && WindowComboBox.SelectedItem is not null && _selectedMonitor is not null;
        StopButton.IsEnabled = active;
    }

    private void UpdateConfigurationAvailability()
    {
        if (!_isInitialized && !IsLoaded)
        {
            return;
        }

        var target = WindowComboBox.SelectedItem as ExternalWindowInfo;
        var canResize = target?.CanResize == true;
        PercentageRadio.IsEnabled = canResize;
        ExactPixelsRadio.IsEnabled = canResize;
        RemoveTitleBarToggle.IsEnabled = target?.HasCaption == true;
        if (target is not null && !target.HasCaption)
        {
            RemoveTitleBarToggle.IsOn = false;
        }
        if (target is not null && !canResize && (PercentageRadio.IsChecked == true || ExactPixelsRadio.IsChecked == true))
        {
            UnchangedRadio.IsChecked = true;
        }

        if (target is null)
        {
            WindowCapabilityText.Text = "対象を選択すると、サイズ変更とタイトルバーの対応状況を表示します。";
        }
        else
        {
            WindowCapabilityText.Text =
                $"サイズ変更: {(target.CanResize ? "対応" : "非対応")}  •  標準タイトルバー: {(target.HasCaption ? "あり" : "なし／カスタム")}";
        }

        UpdateSizeInputAvailability();
        if (!_session.IsActive)
        {
            StartButton.IsEnabled = target is not null && _selectedMonitor is not null;
        }
    }

    private void UpdateSizeInputAvailability()
    {
        PercentageSlider.IsEnabled = PercentageRadio.IsChecked == true && PercentageRadio.IsEnabled;
        ExactWidthTextBox.IsEnabled = ExactPixelsRadio.IsChecked == true && ExactPixelsRadio.IsEnabled;
        ExactHeightTextBox.IsEnabled = ExactPixelsRadio.IsChecked == true && ExactPixelsRadio.IsEnabled;
    }

    private void OnWindowSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateConfigurationAvailability();

    private void OnRefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindows();

    private void OnMonitorCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawMonitorPreview();

    private void OnSizeModeChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateSizeInputAvailability();
        }
    }

    private void OnPercentageChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PercentageText is not null)
        {
            PercentageText.Text = $"{(int)e.NewValue}%";
        }
    }

    private void OnSessionStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        ShowStatus(e.Message, e.Kind);
        if (e.State == SpotlightSessionState.Idle)
        {
            SetSessionControls(false);
            Dispatcher.BeginInvoke(() => RefreshWindows(), DispatcherPriority.Background);
        }
    }

    private void ShowStatus(string message, SessionStatusKind kind)
    {
        StatusText.Text = message;
        var color = kind switch
        {
            SessionStatusKind.Success => Color.FromRgb(16, 124, 65),
            SessionStatusKind.Warning => Color.FromRgb(157, 93, 0),
            SessionStatusKind.Error => Color.FromRgb(196, 43, 28),
            _ => Color.FromRgb(80, 80, 80)
        };
        StatusBadge.Background = new SolidColorBrush(Color.FromArgb(45, color.R, color.G, color.B));
        StatusText.Foreground = new SolidColorBrush(color);
    }

    private void OnPickerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session.IsActive)
        {
            return;
        }

        _isPicking = true;
        _pickerCandidate = null;
        _pickerHighlight = new PickerHighlightWindow();
        Mouse.Capture(PickerButton, CaptureMode.Element);
        _pickerTimer.Start();
        ShowStatus("マウスを押したまま対象へ移動し、離して選択します。Escでキャンセルできます。", SessionStatusKind.Neutral);
        e.Handled = true;
    }

    private void OnPickerTimerTick(object? sender, EventArgs e)
    {
        if (!_isPicking)
        {
            return;
        }

        _pickerHighlight?.Hide();
        var handle = _platform.WindowAtCursor();
        var candidate = _platform.TryGetWindowInfo(handle);
        _pickerCandidate = candidate;
        if (candidate is not null)
        {
            _pickerHighlight?.ShowAround(_platform.GetVisibleFrameRect(candidate.Handle));
            ShowStatus($"選択候補: {candidate.Description}", SessionStatusKind.Neutral);
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPicking)
        {
            return;
        }

        var candidate = _pickerCandidate;
        StopPicker(candidate is null);
        if (candidate is not null)
        {
            RefreshWindows(candidate.Handle);
            ShowStatus($"{candidate.Description} を選択しました。", SessionStatusKind.Success);
        }
        else
        {
            ShowStatus("選択できる外部ウィンドウが見つかりませんでした。", SessionStatusKind.Warning);
        }

        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isPicking && e.Key == Key.Escape)
        {
            StopPicker(true);
            ShowStatus("照準による選択をキャンセルしました。", SessionStatusKind.Neutral);
            e.Handled = true;
        }
    }

    private void StopPicker(bool cancelled)
    {
        if (!_isPicking && _pickerHighlight is null)
        {
            return;
        }

        _isPicking = false;
        _pickerTimer.Stop();
        Mouse.Capture(null);
        _pickerHighlight?.Close();
        _pickerHighlight = null;
        _pickerCandidate = null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshMonitors, DispatcherPriority.Background);
    }
}
