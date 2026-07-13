using System.Windows.Media;

namespace WindowSpotlight;

internal enum SizeMode
{
    Unchanged,
    FitPercentage,
    ExactPixels
}

internal enum SpotlightSessionState
{
    Idle,
    ActiveVisible,
    ActiveSuspended
}

internal readonly record struct PixelPoint(int X, int Y);

internal readonly record struct PixelSize(int Width, int Height)
{
    public bool IsPositive => Width > 0 && Height > 0;
}

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public PixelSize Size => new(Width, Height);

    public static PixelRect FromPositionAndSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);
}

internal readonly record struct WindowFrameInsets(int Left, int Top, int Right, int Bottom)
{
    public static WindowFrameInsets Between(PixelRect windowRect, PixelRect visibleRect) => new(
        Math.Max(0, visibleRect.Left - windowRect.Left),
        Math.Max(0, visibleRect.Top - windowRect.Top),
        Math.Max(0, windowRect.Right - visibleRect.Right),
        Math.Max(0, windowRect.Bottom - visibleRect.Bottom));
}

internal sealed record ExternalWindowInfo(
    nint Handle,
    uint ProcessId,
    string Title,
    string ProcessName,
    bool CanResize,
    bool HasCaption,
    ImageSource? Icon)
{
    public string Description => $"{Title} — {ProcessName}";
}

internal sealed record DisplayMonitorInfo(
    nint Handle,
    string DeviceId,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary)
{
    public string Name
    {
        get
        {
            var suffix = DeviceId.Replace(@"\\.\DISPLAY", string.Empty, StringComparison.OrdinalIgnoreCase);
            return int.TryParse(suffix, out var number) ? $"ディスプレイ {number}" : DeviceId;
        }
    }

    public string Description => $"{Bounds.Width} × {Bounds.Height}" + (IsPrimary ? "  •  メイン" : string.Empty);
}

internal sealed record SpotlightOptions(
    SizeMode SizeMode,
    int FitPercentage,
    int ExactWidth,
    int ExactHeight,
    bool RemoveTitleBar);

internal sealed class PersistedSettings
{
    public string? MonitorDeviceId { get; set; }
    public SizeMode SizeMode { get; set; } = SizeMode.Unchanged;
    public int FitPercentage { get; set; } = 80;
    public int ExactWidth { get; set; } = 1280;
    public int ExactHeight { get; set; } = 720;
    public bool RemoveTitleBar { get; set; }

    public void Normalize()
    {
        if (!Enum.IsDefined(SizeMode))
        {
            SizeMode = SizeMode.Unchanged;
        }

        FitPercentage = Math.Clamp((int)Math.Round(FitPercentage / 5d) * 5, 10, 100);
        ExactWidth = Math.Max(1, ExactWidth);
        ExactHeight = Math.Max(1, ExactHeight);
    }
}

internal sealed record WindowSnapshot(
    NativeMethods.WindowPlacement Placement,
    nint Style,
    nint ExtendedStyle,
    PixelRect WindowRect,
    PixelRect VisibleRect,
    bool WasTopmost);

internal readonly record struct PreviewRect(double Left, double Top, double Width, double Height);
