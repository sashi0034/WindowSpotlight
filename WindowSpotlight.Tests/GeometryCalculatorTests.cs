using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WindowSpotlight.Tests;

[TestClass]
public sealed class GeometryCalculatorTests
{
    [TestMethod]
    public void FitPercentage_AtFiftyPercent_FillsMatchingAspectRatioBox()
    {
        var result = GeometryCalculator.CalculateVisibleSize(
            SizeMode.FitPercentage,
            new PixelSize(1600, 900),
            new PixelSize(1920, 1080),
            50,
            0,
            0);

        Assert.AreEqual(new PixelSize(960, 540), result);
    }

    [TestMethod]
    public void FitPercentage_PreservesAspectRatioInsideBox()
    {
        var result = GeometryCalculator.CalculateVisibleSize(
            SizeMode.FitPercentage,
            new PixelSize(1024, 768),
            new PixelSize(1920, 1080),
            50,
            0,
            0);

        Assert.AreEqual(new PixelSize(720, 540), result);
    }

    [TestMethod]
    public void ExactPixels_ClampsToMonitor()
    {
        var result = GeometryCalculator.CalculateVisibleSize(
            SizeMode.ExactPixels,
            new PixelSize(800, 600),
            new PixelSize(1080, 1920),
            80,
            1400,
            900);

        Assert.AreEqual(new PixelSize(1080, 900), result);
    }

    [TestMethod]
    public void Center_HandlesNegativeMonitorCoordinates()
    {
        var monitor = new PixelRect(-1920, 0, 0, 1080);
        var result = GeometryCalculator.Center(monitor, new PixelSize(960, 540));

        Assert.AreEqual(new PixelRect(-1440, 270, -480, 810), result);
    }

    [TestMethod]
    public void VisibleToWindowRect_AddsInvisibleFrameInsets()
    {
        var visible = new PixelRect(480, 270, 1440, 810);
        var result = GeometryCalculator.VisibleToWindowRect(visible, new WindowFrameInsets(8, 1, 8, 8));

        Assert.AreEqual(new PixelRect(472, 269, 1448, 818), result);
    }

    [TestMethod]
    public void MonitorPreview_PreservesRelativePlacementAcrossNegativeCoordinates()
    {
        DisplayMonitorInfo[] monitors =
        [
            new(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true),
            new(2, @"\\.\DISPLAY2", new PixelRect(-1080, 0, 0, 1920), new PixelRect(-1080, 0, 0, 1880), false)
        ];

        var result = GeometryCalculator.CalculateMonitorPreview(monitors, 600, 300, 10);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result[@"\\.\DISPLAY2"].Left < result[@"\\.\DISPLAY1"].Left);
        Assert.AreEqual(
            result[@"\\.\DISPLAY1"].Left,
            result[@"\\.\DISPLAY2"].Left + result[@"\\.\DISPLAY2"].Width,
            0.001);
    }
}
