using MirrorPulse.Adapter.Smb.Worker;

namespace MirrorPulse.Adapter.Smb.Worker.Tests;

[TestClass]
public sealed class SmbWorkerPathTests
{
    [TestMethod]
    [DataRow("C:\\data")]
    [DataRow("relative")]
    [DataRow("\\\\server")]
    [DataRow("\\\\?\\UNC\\server\\share")]
    [DataRow("\\\\.\\device")]
    [DataRow("\\\\server\\share\\..\\other")]
    [DataRow("\\\\server\\share\\file:stream")]
    [DataRow("\\\\server\\share/other")]
    [DataRow("\\\\server\\share\\bad.")]
    [DataRow("\\\\server\\share\\bad ")]
    [DataRow("\\\\server\\share\\\\other")]
    public void UnsafeNetworkPathsAreRejectedBeforeAccess(string path) =>
        Assert.ThrowsExactly<InvalidDataException>(() => SmbWorkerPaths.NormalizeNetworkPath(path));

    [TestMethod]
    public void CanonicalShareAndSubdirectoryPathsRemainAuthorized()
    {
        Assert.AreEqual("\\\\server\\share", SmbWorkerPaths.NormalizeNetworkPath("\\\\server\\share\\"));
        Assert.AreEqual("\\\\server\\share\\授权", SmbWorkerPaths.NormalizeNetworkPath("\\\\server\\share\\授权"));
    }
}
