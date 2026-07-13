using System.Windows.Threading;

namespace WindowSpotlight;

internal sealed class SpotlightSession : IDisposable
{
    private readonly WindowPlatform _platform;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _geometryTimer;
    private BackdropWindow? _backdrop;
    private WinEventWatcher? _eventWatcher;
    private ExternalWindowInfo? _target;
    private DisplayMonitorInfo? _monitor;
    private SpotlightOptions? _options;
    private WindowSnapshot? _snapshot;
    private PixelRect _desiredWindowRect;
    private PixelSize _requestedVisibleSize;
    private bool _initialGeometryPending;
    private bool _isApplyingGeometry;
    private bool _disposed;

    public SpotlightSession(WindowPlatform platform, Dispatcher dispatcher)
    {
        _platform = platform;
        _dispatcher = dispatcher;
        _geometryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _geometryTimer.Tick += OnGeometryTimerTick;
    }

    public SpotlightSessionState State { get; private set; } = SpotlightSessionState.Idle;
    public bool IsActive => State != SpotlightSessionState.Idle;

    public event EventHandler<SessionStatusEventArgs>? StatusChanged;

    public void Start(ExternalWindowInfo target, DisplayMonitorInfo monitor, SpotlightOptions options)
    {
        if (IsActive)
        {
            throw new InvalidOperationException("すでにスポットライトを実行しています。");
        }

        if (!_platform.IsWindow(target.Handle))
        {
            throw new InvalidOperationException("選択したウィンドウはすでに閉じられています。");
        }

        if (options.SizeMode != SizeMode.Unchanged && !target.CanResize)
        {
            throw new InvalidOperationException("このウィンドウは標準のサイズ変更に対応していません。");
        }

        _target = target;
        _monitor = monitor;
        _options = options;
        _snapshot = _platform.CaptureSnapshot(target.Handle);

        try
        {
            _platform.RestoreForPositioning(target.Handle);
            if (options.RemoveTitleBar)
            {
                if (!target.HasCaption)
                {
                    throw new InvalidOperationException("このウィンドウには削除できる標準タイトルバーがありません。");
                }

                _platform.RemoveCaption(target.Handle);
            }

            ApplyInitialGeometry();
            _backdrop = new BackdropWindow();
            _eventWatcher = new WinEventWatcher(target.ProcessId);
            _eventWatcher.EventReceived += OnWinEventReceived;

            ShowBackdropAndRaiseTarget();
            var activated = _platform.Activate(target.Handle);
            EvaluateForeground(_platform.ForegroundWindow);
            if (!activated || State == SpotlightSessionState.ActiveSuspended)
            {
                SetState(
                    SpotlightSessionState.ActiveSuspended,
                    "実行を開始しました。対象をクリックすると黒背景が表示されます。",
                    SessionStatusKind.Warning);
            }
            else
            {
                SetState(
                    SpotlightSessionState.ActiveVisible,
                    "スポットライトを実行中です。別のアプリへ切り替えると背景を隠します。",
                    SessionStatusKind.Success);
            }

            _initialGeometryPending = true;
            _geometryTimer.Stop();
            _geometryTimer.Interval = TimeSpan.FromMilliseconds(300);
            _geometryTimer.Start();
        }
        catch
        {
            CleanupHooksAndBackdrop();
            if (_snapshot is not null && _platform.IsWindow(target.Handle))
            {
                _platform.RestoreWindow(target.Handle, _snapshot);
            }

            ResetState();
            throw;
        }
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        var targetHandle = _target?.Handle ?? 0;
        var snapshot = _snapshot;
        CleanupHooksAndBackdrop();
        try
        {
            if (targetHandle != 0 && snapshot is not null && _platform.IsWindow(targetHandle))
            {
                _platform.RestoreWindow(targetHandle, snapshot);
            }
        }
        finally
        {
            ResetState();
            SetState(SpotlightSessionState.Idle, "停止しました。対象ウィンドウを元の状態へ戻しました。", SessionStatusKind.Neutral);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _geometryTimer.Tick -= OnGeometryTimerTick;
        GC.SuppressFinalize(this);
    }

    private void ApplyInitialGeometry()
    {
        var target = _target!;
        var monitor = _monitor!;
        var options = _options!;
        var snapshot = _snapshot!;
        var insets = WindowFrameInsets.Between(snapshot.WindowRect, snapshot.VisibleRect);

        _requestedVisibleSize = GeometryCalculator.CalculateVisibleSize(
            options.SizeMode,
            snapshot.VisibleRect.Size,
            monitor.Bounds.Size,
            options.FitPercentage,
            options.ExactWidth,
            options.ExactHeight);
        var desiredVisibleRect = GeometryCalculator.Center(monitor.Bounds, _requestedVisibleSize);
        _desiredWindowRect = GeometryCalculator.VisibleToWindowRect(desiredVisibleRect, insets);
        ApplyExpectedGeometry();
    }

    private void ApplyExpectedGeometry()
    {
        if (_target is null || !_platform.IsWindow(_target.Handle) || _isApplyingGeometry)
        {
            return;
        }

        _isApplyingGeometry = true;
        try
        {
            _platform.PositionWindow(_target.Handle, _desiredWindowRect);
        }
        finally
        {
            _isApplyingGeometry = false;
        }
    }

    private void SettleGeometry()
    {
        if (_target is null || _monitor is null)
        {
            return;
        }
        if (!_platform.IsWindow(_target.Handle))
        {
            EndBecauseTargetClosed();
            return;
        }

        var actualWindow = _platform.GetWindowRect(_target.Handle);
        var actualVisible = _platform.GetVisibleFrameRect(_target.Handle);
        var insets = WindowFrameInsets.Between(actualWindow, actualVisible);
        var centeredVisible = GeometryCalculator.Center(_monitor.Bounds, actualVisible.Size);
        _desiredWindowRect = GeometryCalculator.VisibleToWindowRect(centeredVisible, insets);

        var sizeRejected = _options?.SizeMode != SizeMode.Unchanged &&
                           (Math.Abs(actualVisible.Width - _requestedVisibleSize.Width) > 2 ||
                            Math.Abs(actualVisible.Height - _requestedVisibleSize.Height) > 2);
        ApplyExpectedGeometry();

        if (sizeRejected)
        {
            StatusChanged?.Invoke(this, new SessionStatusEventArgs(
                State,
                $"対象アプリの制約により {actualVisible.Width} × {actualVisible.Height}px で表示しています。",
                SessionStatusKind.Warning));
        }
    }

    private void OnWinEventReceived(object? sender, WinEventArgs e)
    {
        _dispatcher.BeginInvoke(() => HandleWinEvent(e), DispatcherPriority.Send);
    }

    private void HandleWinEvent(WinEventArgs e)
    {
        if (!IsActive || _target is null)
        {
            return;
        }

        if (e.EventType == NativeMethods.EventObjectDestroy && e.Window == _target.Handle &&
            e.ObjectId == NativeMethods.ObjidWindow)
        {
            EndBecauseTargetClosed();
            return;
        }

        if (e.EventType == NativeMethods.EventSystemForeground)
        {
            EvaluateForeground(e.Window);
            return;
        }

        if (e.EventType == NativeMethods.EventObjectLocationChange && e.Window == _target.Handle &&
            e.ObjectId == NativeMethods.ObjidWindow && !_isApplyingGeometry)
        {
            _geometryTimer.Stop();
            _geometryTimer.Interval = TimeSpan.FromMilliseconds(180);
            _geometryTimer.Start();
        }
    }

    private void EvaluateForeground(nint foreground)
    {
        if (_target is null || _snapshot is null)
        {
            return;
        }

        if (!_platform.IsWindow(_target.Handle))
        {
            EndBecauseTargetClosed();
            return;
        }

        if (ForegroundClassifier.IsTargetOrOwned(foreground, _target.Handle, _platform.GetOwner))
        {
            ShowBackdropAndRaiseTarget();
            SetState(SpotlightSessionState.ActiveVisible, "スポットライトを実行中です。", SessionStatusKind.Success);
        }
        else
        {
            _backdrop?.Hide();
            if (!_snapshot.WasTopmost && _platform.IsWindow(_target.Handle))
            {
                _platform.SetTemporaryTopmost(_target.Handle, false);
            }

            SetState(SpotlightSessionState.ActiveSuspended, "対象が前面でないため、黒背景を一時的に隠しています。", SessionStatusKind.Neutral);
        }
    }

    private void ShowBackdropAndRaiseTarget()
    {
        if (_backdrop is null || _monitor is null || _target is null)
        {
            return;
        }

        _backdrop.ShowAt(_monitor.Bounds);
        _platform.SetTemporaryTopmost(_target.Handle, true);
    }

    private void OnGeometryTimerTick(object? sender, EventArgs e)
    {
        _geometryTimer.Stop();
        if (_initialGeometryPending)
        {
            _initialGeometryPending = false;
            SettleGeometry();
            return;
        }

        if (_target is null)
        {
            return;
        }

        if (!_platform.IsWindow(_target.Handle))
        {
            EndBecauseTargetClosed();
            return;
        }

        var current = _platform.GetWindowRect(_target.Handle);
        if (Math.Abs(current.Left - _desiredWindowRect.Left) > 1 ||
            Math.Abs(current.Top - _desiredWindowRect.Top) > 1 ||
            Math.Abs(current.Width - _desiredWindowRect.Width) > 1 ||
            Math.Abs(current.Height - _desiredWindowRect.Height) > 1)
        {
            ApplyExpectedGeometry();
        }
    }

    private void CleanupHooksAndBackdrop()
    {
        _geometryTimer.Stop();
        if (_eventWatcher is not null)
        {
            _eventWatcher.EventReceived -= OnWinEventReceived;
            _eventWatcher.Dispose();
            _eventWatcher = null;
        }

        if (_backdrop is not null)
        {
            _backdrop.Close();
            _backdrop = null;
        }
    }

    private void EndBecauseTargetClosed()
    {
        CleanupHooksAndBackdrop();
        ResetState();
        SetState(
            SpotlightSessionState.Idle,
            "対象ウィンドウが閉じられたため停止しました。",
            SessionStatusKind.Warning);
    }

    private void ResetState()
    {
        _target = null;
        _monitor = null;
        _options = null;
        _snapshot = null;
        _initialGeometryPending = false;
        _desiredWindowRect = default;
        State = SpotlightSessionState.Idle;
    }

    private void SetState(SpotlightSessionState state, string message, SessionStatusKind kind)
    {
        State = state;
        StatusChanged?.Invoke(this, new SessionStatusEventArgs(state, message, kind));
    }
}

internal enum SessionStatusKind
{
    Neutral,
    Success,
    Warning,
    Error
}

internal sealed record SessionStatusEventArgs(
    SpotlightSessionState State,
    string Message,
    SessionStatusKind Kind);
