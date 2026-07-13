using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace WindowSpotlight.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "WindowSpotlight.Tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public void SaveAndLoad_RoundTripsValuesAndNormalizesPercentageToFivePercentStep()
    {
        var service = new SettingsService(_path);
        service.Save(new PersistedSettings
        {
            MonitorDeviceId = @"\\.\DISPLAY2",
            SizeMode = SizeMode.FitPercentage,
            FitPercentage = 83,
            ExactWidth = 1600,
            ExactHeight = 900,
            RemoveTitleBar = true
        });

        var result = service.Load();

        Assert.AreEqual(@"\\.\DISPLAY2", result.MonitorDeviceId);
        Assert.AreEqual(SizeMode.FitPercentage, result.SizeMode);
        Assert.AreEqual(85, result.FitPercentage);
        Assert.AreEqual(1600, result.ExactWidth);
        Assert.AreEqual(900, result.ExactHeight);
        Assert.IsTrue(result.RemoveTitleBar);
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_path, "{ not json");

        var result = new SettingsService(_path).Load();

        Assert.AreEqual(SizeMode.Unchanged, result.SizeMode);
        Assert.AreEqual(80, result.FitPercentage);
        Assert.IsFalse(result.RemoveTitleBar);
    }
}
