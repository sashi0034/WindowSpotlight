namespace WindowSpotlight;

internal static class GeometryCalculator
{
    public static PixelSize CalculateVisibleSize(
        SizeMode mode,
        PixelSize currentSize,
        PixelSize monitorSize,
        int fitPercentage,
        int exactWidth,
        int exactHeight)
    {
        if (!currentSize.IsPositive || !monitorSize.IsPositive)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSize));
        }

        return mode switch
        {
            SizeMode.Unchanged => currentSize,
            SizeMode.ExactPixels => new PixelSize(
                Math.Clamp(exactWidth, 1, monitorSize.Width),
                Math.Clamp(exactHeight, 1, monitorSize.Height)),
            SizeMode.FitPercentage => FitPreservingAspectRatio(
                currentSize,
                new PixelSize(
                    Math.Max(1, monitorSize.Width * Math.Clamp(fitPercentage, 10, 100) / 100),
                    Math.Max(1, monitorSize.Height * Math.Clamp(fitPercentage, 10, 100) / 100))),
            _ => currentSize
        };
    }

    public static PixelRect Center(PixelRect container, PixelSize size)
    {
        var left = container.Left + (container.Width - size.Width) / 2;
        var top = container.Top + (container.Height - size.Height) / 2;
        return PixelRect.FromPositionAndSize(left, top, size.Width, size.Height);
    }

    public static PixelRect VisibleToWindowRect(PixelRect visibleRect, WindowFrameInsets insets) =>
        new(
            visibleRect.Left - insets.Left,
            visibleRect.Top - insets.Top,
            visibleRect.Right + insets.Right,
            visibleRect.Bottom + insets.Bottom);

    public static IReadOnlyDictionary<string, PreviewRect> CalculateMonitorPreview(
        IReadOnlyList<DisplayMonitorInfo> monitors,
        double availableWidth,
        double availableHeight,
        double padding = 10)
    {
        if (monitors.Count == 0 || availableWidth <= padding * 2 || availableHeight <= padding * 2)
        {
            return new Dictionary<string, PreviewRect>();
        }

        var virtualLeft = monitors.Min(m => m.Bounds.Left);
        var virtualTop = monitors.Min(m => m.Bounds.Top);
        var virtualRight = monitors.Max(m => m.Bounds.Right);
        var virtualBottom = monitors.Max(m => m.Bounds.Bottom);
        var virtualWidth = Math.Max(1, virtualRight - virtualLeft);
        var virtualHeight = Math.Max(1, virtualBottom - virtualTop);
        var scale = Math.Min(
            (availableWidth - padding * 2) / virtualWidth,
            (availableHeight - padding * 2) / virtualHeight);
        var contentWidth = virtualWidth * scale;
        var contentHeight = virtualHeight * scale;
        var offsetX = (availableWidth - contentWidth) / 2;
        var offsetY = (availableHeight - contentHeight) / 2;

        return monitors.ToDictionary(
            m => m.DeviceId,
            m => new PreviewRect(
                offsetX + (m.Bounds.Left - virtualLeft) * scale,
                offsetY + (m.Bounds.Top - virtualTop) * scale,
                Math.Max(1, m.Bounds.Width * scale),
                Math.Max(1, m.Bounds.Height * scale)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static PixelSize FitPreservingAspectRatio(PixelSize current, PixelSize box)
    {
        var scale = Math.Min(box.Width / (double)current.Width, box.Height / (double)current.Height);
        return new PixelSize(
            Math.Max(1, (int)Math.Round(current.Width * scale)),
            Math.Max(1, (int)Math.Round(current.Height * scale)));
    }
}
