using System.Diagnostics;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker.Tests;

[TestClass]
[TestCategory("SmbNative")]
public sealed class SmbWorkerAvailabilityTests
{
    [TestMethod]
    public async Task AWholeShareRootCanBeStatedAndReadWithinItsAuthorization()
    {
        await using var session = await SmbWorkerSession.StartAsync(shareRoot: true);
        AdapterControlFrame root = await session.RequestAsync("Stat", new { rootKey = "left", path = "" });
        Assert.AreEqual("StatResult", root.MessageType);
        Assert.IsTrue(root.Payload.GetProperty("revision").GetString()!.StartsWith("directory:", StringComparison.Ordinal));
        string relative = Path.GetFileName(session.Root) + "/left/same.txt";
        Assert.AreEqual("left", System.Text.Encoding.UTF8.GetString(await session.ReadRangeAsync("left", relative, 4)));
    }

    [TestMethod]
    public async Task BoundedDeepPathsUseNativeExtendedUncNamesWithoutLosingContent()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string relative = string.Join('/', Enumerable.Range(0, 12).Select(index => "directory-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + new string('x', 20)));
        Directory.CreateDirectory(Path.Combine(session.Root, "left", relative.Replace('/', '\\')));
        string file = relative + "/long.txt";
        Assert.IsGreaterThan(260, Path.Combine(session.Root, "left", file).Length);
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", file, "long-path-content"u8.ToArray())).MessageType);
        Assert.AreEqual("long-path-content", System.Text.Encoding.UTF8.GetString(await session.ReadRangeAsync("left", file, 17)));
        AdapterControlFrame current = await session.RequestAsync("Stat", new { rootKey = "left", path = file });
        var move = new AdapterOperationRequest(Guid.NewGuid(), "left", file, "left", relative + "/moved.txt",
            new(current.Payload.GetProperty("revision").GetString()));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Move", move)).MessageType);
        Assert.AreEqual("long-path-content", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", relative.Replace('/', '\\'), "moved.txt")));
    }

    [TestMethod]
    public async Task ADisconnectedSharePreservesSourceAndOtherRootAndRecoversWithoutRestart()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        await TransitionAsync("-DisconnectLeft");
        try
        {
            AdapterControlFrame unavailable = await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" });
            Assert.AreEqual("OperationError", unavailable.MessageType);
            Assert.IsTrue(unavailable.Payload.GetProperty("code").GetString() is "SourceUnavailable" or "RetryableTransferFailure");
            Assert.AreEqual("right", System.Text.Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
            Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        }
        finally { await TransitionAsync("-ReconnectLeft"); }
        Assert.AreEqual("StatResult", (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).MessageType);
    }

    private static async Task TransitionAsync(string transition)
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "eng", "setup-smb-fixture.ps1")))
            repository = Directory.GetParent(repository)?.FullName;
        Assert.IsNotNull(repository);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "eng", "setup-smb-fixture.ps1"), transition })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }
}
