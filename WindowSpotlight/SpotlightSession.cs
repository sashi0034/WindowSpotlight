using System.Windows.Threading;

namespace WindowSpotlight;

internal sealed class SpotlightSession : IDisposable
{
    private readonly WindowPlatform _platform;
    private readonly Dispatcher _dispatcher;
    private BackdropWindow? _backdrop;
    private WinEventWatcher? _eventWatcher;
    private ExternalWindowInfo? _target;
    private DisplayMonitorInfo? _monitor;
    private SpotlightOptions? _options;
    private WindowSnapshot? _snapshot;
    private bool _disposed;

    public SpotlightSession(WindowPlatform platform, Dispatcher dispatcher)
    {
        _platform = platform;
        _dispatcher = dispatcher;
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
        GC.SuppressFinalize(this);
    }

    public void Recenter()
    {
        if (!IsActive || _target is null || _monitor is null)
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
        var centeredWindow = GeometryCalculator.VisibleToWindowRect(centeredVisible, insets);
        _platform.PositionWindow(_target.Handle, centeredWindow);

        StatusChanged?.Invoke(this, new SessionStatusEventArgs(
            State,
            "対象ウィンドウをディスプレイ中央へ再配置しました。",
            SessionStatusKind.Success));
    }

    public void UpdateSize(SpotlightOptions options)
    {
        if (!IsActive || _target is null || _monitor is null || _snapshot is null ||
            options.SizeMode == SizeMode.Unchanged)
        {
            return;
        }

        if (!_target.CanResize)
        {
            throw new InvalidOperationException("このウィンドウは標準のサイズ変更に対応していません。");
        }

        if (!_platform.IsWindow(_target.Handle))
        {
            EndBecauseTargetClosed();
            return;
        }

        var actualWindow = _platform.GetWindowRect(_target.Handle);
        var actualVisible = _platform.GetVisibleFrameRect(_target.Handle);
        var insets = WindowFrameInsets.Between(actualWindow, actualVisible);
        var requestedVisibleSize = GeometryCalculator.CalculateVisibleSize(
            options.SizeMode,
            _snapshot.VisibleRect.Size,
            _monitor.Bounds.Size,
            options.FitPercentage,
            options.ExactWidth,
            options.ExactHeight);
        var resizedVisible = GeometryCalculator.Center(_monitor.Bounds, requestedVisibleSize);
        var resizedWindow = GeometryCalculator.VisibleToWindowRect(resizedVisible, insets);

        _options = options;
        _platform.PositionWindow(_target.Handle, resizedWindow);
        StatusChanged?.Invoke(this, new SessionStatusEventArgs(
            State,
            $"対象ウィンドウを {requestedVisibleSize.Width} × {requestedVisibleSize.Height}px に変更し、中央へ再配置しました。",
            SessionStatusKind.Success));
    }

    private void ApplyInitialGeometry()
    {
        var target = _target!;
        var monitor = _monitor!;
        var options = _options!;
        var snapshot = _snapshot!;
        var currentWindow = _platform.GetWindowRect(target.Handle);
        var currentVisible = _platform.GetVisibleFrameRect(target.Handle);
        var insets = WindowFrameInsets.Between(currentWindow, currentVisible);

        var requestedVisibleSize = GeometryCalculator.CalculateVisibleSize(
            options.SizeMode,
            snapshot.VisibleRect.Size,
            monitor.Bounds.Size,
            options.FitPercentage,
            options.ExactWidth,
            options.ExactHeight);
        var desiredVisibleRect = GeometryCalculator.Center(monitor.Bounds, requestedVisibleSize);
        var desiredWindowRect = GeometryCalculator.VisibleToWindowRect(desiredVisibleRect, insets);
        _platform.PositionWindow(target.Handle, desiredWindowRect);
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

    private void CleanupHooksAndBackdrop()
    {
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
