using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowSpotlight;

internal sealed class WindowPlatform
{
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public IReadOnlyList<ExternalWindowInfo> EnumerateWindows()
    {
        var windows = new List<ExternalWindowInfo>();
        NativeMethods.EnumWindows((window, _) =>
        {
            var info = TryGetWindowInfo(window);
            if (info is not null)
            {
                windows.Add(info);
            }

            return true;
        }, 0);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ExternalWindowInfo? TryGetWindowInfo(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _ownProcessId)
        {
            return null;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
        if ((extendedStyle & NativeMethods.WsExToolWindow) != 0 || IsCloaked(window))
        {
            return null;
        }

        var titleLength = NativeMethods.GetWindowTextLength(window);
        if (titleLength <= 0)
        {
            return null;
        }

        var titleBuilder = new System.Text.StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(window, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString().Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var processName = "不明なアプリ";
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }

        var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle).ToInt64();
        return new ExternalWindowInfo(
            window,
            processId,
            title,
            processName,
            (style & NativeMethods.WsThickFrame) != 0,
            (style & NativeMethods.WsCaption) != 0,
            TryGetWindowIcon(window));
    }

    public IReadOnlyList<DisplayMonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<DisplayMonitorInfo>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint monitorDc, ref NativeMethods.NativeRect monitorRect, nint parameter) =>
        {
            var info = NativeMethods.MonitorInfoEx.Create();
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new DisplayMonitorInfo(
                    monitor,
                    info.DeviceName,
                    info.Monitor.ToPixelRect(),
                    info.WorkArea.ToPixelRect(),
                    (info.Flags & NativeMethods.MonitorInfoPrimary) != 0));
            }

            return true;
        }, 0);
        return monitors.OrderBy(monitor => monitor.Bounds.Left).ThenBy(monitor => monitor.Bounds.Top).ToArray();
    }

    public nint WindowAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return 0;
        }

        var window = NativeMethods.WindowFromPoint(point);
        return window == 0 ? 0 : NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
    }

    public PixelPoint CursorPosition()
    {
        return NativeMethods.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : default;
    }

    public bool IsWindow(nint window) => NativeMethods.IsWindow(window);

    public nint ForegroundWindow => NativeMethods.GetForegroundWindow();

    public nint GetOwner(nint window) => NativeMethods.GetWindow(window, NativeMethods.GwOwner);

    public PixelRect GetWindowRect(nint window)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            throw CreateWin32Exception("ウィンドウの位置を取得できませんでした。");
        }

        return rect.ToPixelRect();
    }

    public PixelRect GetVisibleFrameRect(nint window)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmwaExtendedFrameBounds,
                out NativeMethods.NativeRect frame,
                Marshal.SizeOf<NativeMethods.NativeRect>()) == 0 && frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            return frame.ToPixelRect();
        }

        return GetWindowRect(window);
    }

    public WindowSnapshot CaptureSnapshot(nint window)
    {
        var placement = NativeMethods.WindowPlacement.Create();
        if (!NativeMethods.GetWindowPlacement(window, ref placement))
        {
            throw CreateWin32Exception("ウィンドウの表示状態を取得できませんでした。");
        }

        var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var extendedStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
        var windowRect = GetWindowRect(window);
        var visibleRect = GetVisibleFrameRect(window);
        if (placement.ShowCommand != NativeMethods.SwShowNormal &&
            placement.NormalPosition.Right > placement.NormalPosition.Left &&
            placement.NormalPosition.Bottom > placement.NormalPosition.Top)
        {
            windowRect = placement.NormalPosition.ToPixelRect();
            visibleRect = windowRect;
        }

        return new WindowSnapshot(
            placement,
            style,
            extendedStyle,
            windowRect,
            visibleRect,
            (extendedStyle.ToInt64() & NativeMethods.WsExTopmost) != 0);
    }

    public void RestoreWindow(nint window, WindowSnapshot snapshot)
    {
        if (!IsWindow(window))
        {
            return;
        }

        SetStyle(window, NativeMethods.GwlStyle, snapshot.Style);
        SetStyle(window, NativeMethods.GwlExStyle, snapshot.ExtendedStyle);
        NativeMethods.SetWindowPos(
            window,
            snapshot.WasTopmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        var placement = snapshot.Placement;
        NativeMethods.SetWindowPlacement(window, ref placement);
    }

    public void RemoveCaption(nint window)
    {
        var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var newStyle = new nint(style.ToInt64() & ~NativeMethods.WsCaption);
        SetStyle(window, NativeMethods.GwlStyle, newStyle);
        if (!NativeMethods.SetWindowPos(
                window,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged))
        {
            throw CreateWin32Exception("タイトルバーを変更できませんでした。");
        }
    }

    public void RestoreForPositioning(nint window) => NativeMethods.ShowWindowAsync(window, NativeMethods.SwRestore);

    public void PositionWindow(nint window, PixelRect rect)
    {
        var flags = NativeMethods.SwpAsyncWindowPos | NativeMethods.SwpShowWindow |
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder;

        if (!NativeMethods.SetWindowPos(window, 0, rect.Left, rect.Top, rect.Width, rect.Height, flags))
        {
            throw CreateWin32Exception("対象ウィンドウを移動できませんでした。");
        }
    }

    public void SetTemporaryTopmost(nint window, bool topmost)
    {
        NativeMethods.SetWindowPos(
            window,
            topmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate |
            NativeMethods.SwpAsyncWindowPos);
    }

    public bool Activate(nint window) => NativeMethods.SetForegroundWindow(window);

    private static bool IsCloaked(nint window)
    {
        return NativeMethods.DwmGetWindowAttribute(
            window,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            sizeof(int)) == 0 && cloaked != 0;
    }

    private static ImageSource? TryGetWindowIcon(nint window)
    {
        nint icon = 0;
        if (NativeMethods.SendMessageTimeout(
                window,
                NativeMethods.WmGetIcon,
                (nint)NativeMethods.IconSmall2,
                0,
                NativeMethods.SmtoAbortIfHung,
                50,
                out var result) != 0)
        {
            icon = result;
        }

        icon = icon != 0 ? icon : NativeMethods.GetClassLongPtr(window, NativeMethods.GclpHIconSm);
        icon = icon != 0 ? icon : NativeMethods.GetClassLongPtr(window, NativeMethods.GclpHIcon);
        if (icon == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static void SetStyle(nint window, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = NativeMethods.SetWindowLongPtr(window, index, value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw CreateWin32Exception("ウィンドウのスタイルを変更できませんでした。");
        }
    }

    private static Win32Exception CreateWin32Exception(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, error == 5 ? $"{message} 管理者権限の対象は同じ権限で実行してください。" : message);
    }
}
