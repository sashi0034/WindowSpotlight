using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WindowSpotlight.Tests;

[TestClass]
public sealed class ForegroundClassifierTests
{
    [TestMethod]
    public void ExactTarget_IsActive()
    {
        Assert.IsTrue(ForegroundClassifier.IsTargetOrOwned(10, 10, _ => 0));
    }

    [TestMethod]
    public void OwnedDialog_IsActive()
    {
        var owners = new Dictionary<nint, nint> { [30] = 20, [20] = 10 };

        var active = ForegroundClassifier.IsTargetOrOwned(
            30,
            10,
            window => owners.GetValueOrDefault(window));

        Assert.IsTrue(active);
    }

    [TestMethod]
    public void UnrelatedWindow_IsInactive()
    {
        Assert.IsFalse(ForegroundClassifier.IsTargetOrOwned(99, 10, _ => 0));
    }

    [TestMethod]
    public void CyclicOwnerChain_TerminatesSafely()
    {
        var owners = new Dictionary<nint, nint> { [20] = 30, [30] = 20 };

        var active = ForegroundClassifier.IsTargetOrOwned(
            20,
            10,
            window => owners.GetValueOrDefault(window));

        Assert.IsFalse(active);
    }
}
