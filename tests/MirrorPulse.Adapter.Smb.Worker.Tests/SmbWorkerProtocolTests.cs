using System.Text;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker.Tests;

[TestClass]
[TestCategory("SmbNative")]
public sealed class SmbWorkerProtocolTests
{
    private static readonly string[] EnabledCredentialRoots = ["left", "right"];

    [TestMethod]
    public async Task AStaleReadRevisionIsRejectedBeforeAnyContentFrame()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        AdapterControlFrame current = await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" });
        string expectedRevision = current.Payload.GetProperty("revision").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(session.Root, "left", "same.txt"), "changed");
        AdapterControlFrame conflict = await session.RequestAsync("ReadRange", new { rootKey = "left", path = "same.txt", offset = 0, length = 4, expectedRevision });
        Assert.AreEqual("OperationError", conflict.MessageType);
        Assert.AreEqual("RemoteConflict", conflict.Payload.GetProperty("code").GetString());
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }
    [TestMethod]
    public async Task SeparateCredentialsCannotEnterTheOtherShare()
    {
        await using var session = await SmbWorkerSession.StartAsync(wrongShare: true);
        AdapterControlFrame denied = session.StartupFrame!.MessageType == "Error" ? session.StartupFrame :
            await session.RequestAsync("Stat", new { rootKey = "right", path = "same.txt" });
        Assert.IsTrue(denied.MessageType is "Error" or "OperationError");
        Assert.IsTrue(denied.Payload.GetProperty("code").GetString() is "SourceUnavailable" or "AccessDenied");
        CollectionAssert.AreEqual(EnabledCredentialRoots, session.CredentialRoots);
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        Assert.AreEqual("right", await File.ReadAllTextAsync(Path.Combine(session.Root, "right", "same.txt")));
    }

    [TestMethod]
    public async Task CurrentWindowsIdentityWorksWithoutRequestingCredentials()
    {
        await using var session = await SmbWorkerSession.StartAsync(currentIdentity: true);
        Assert.IsEmpty(session.CredentialRoots);
        Assert.AreEqual("left", Encoding.UTF8.GetString(await session.ReadRangeAsync("left", "same.txt", 4)));
    }

    [TestMethod]
    public async Task TwoAuthorizedSourcesKeepIdenticalPathsAndTransfersIndependent()
    {
        await using var session = await SmbWorkerSession.StartAsync(privatePayload: true);
        CollectionAssert.AreEqual(EnabledCredentialRoots, session.CredentialRoots);
        foreach (string root in new[] { "left", "right" })
        {
            AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = root, path = "", pageSize = 1 });
            Assert.AreEqual("DirectoryPage", page.MessageType);
            Assert.AreEqual("same.txt", page.Payload.GetProperty("entries")[0].GetProperty("remoteId").GetString());
            Assert.AreEqual(root.Length, page.Payload.GetProperty("entries")[0].GetProperty("length").GetInt32());
            AdapterControlFrame stat = await session.RequestAsync("Stat", new { rootKey = root, path = "same.txt" });
            Assert.AreEqual(stat.Payload.GetProperty("revision").GetString(), page.Payload.GetProperty("entries")[0].GetProperty("remoteRevision").GetString());
            Assert.AreEqual(File.GetCreationTimeUtc(Path.Combine(session.Root, root, "same.txt")),
                page.Payload.GetProperty("entries")[0].GetProperty("creationTime").GetDateTimeOffset().UtcDateTime);
            Assert.AreEqual(root, Encoding.UTF8.GetString(await session.ReadRangeAsync(root, "same.txt", root.Length)));
            byte[] bytes = Encoding.UTF8.GetBytes("uploaded-" + root);
            Assert.AreEqual("UploadComplete", (await session.UploadAsync(root, "new.txt", bytes)).MessageType);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(session.Root, root, "new.txt")));
        }
        Assert.IsFalse(Directory.EnumerateFiles(session.Cache).Any());
    }

    [TestMethod]
    public async Task UnknownOfflineAndEscapingAddressesAreRejectedWithoutTouchingSources()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        foreach (var item in new[] { ("unknown", "same.txt", "UnknownRoot"), ("offline", "same.txt", "RootOffline"),
            ("left", "../right/same.txt", "InvalidPath") })
        {
            AdapterControlFrame error = await session.RequestAsync("Stat", new { rootKey = item.Item1, path = item.Item2 });
            Assert.AreEqual("OperationError", error.MessageType);
            Assert.AreEqual(item.Item3, error.Payload.GetProperty("code").GetString());
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(session.Root, "missing-offline")));
        Assert.AreEqual("right", await File.ReadAllTextAsync(Path.Combine(session.Root, "right", "same.txt")));
    }

    [TestMethod]
    public async Task PaginationCursorsCannotBeReplayedAgainstAnotherRoot()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        AdapterControlFrame first = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 1 });
        string cursor = first.Payload.GetProperty("cursor").GetString()!;
        AdapterControlFrame second = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 1, cursor });
        Assert.AreEqual("second.txt", second.Payload.GetProperty("entries")[0].GetProperty("remoteId").GetString());
        AdapterControlFrame wrongRoot = await session.RequestAsync("List", new { rootKey = "right", path = "", pageSize = 1, cursor });
        Assert.AreEqual("InvalidCursor", wrongRoot.Payload.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StableUploadReplayChecksContentAndPreservesExistingDestination()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        Guid operation = Guid.NewGuid();
        byte[] bytes = new byte[AdapterBinaryChunkV2Codec.MaximumChunkBytes + 17];
        Random.Shared.NextBytes(bytes);
        AdapterControlFrame original = await session.UploadAsync("left", "replay.bin", bytes, operation);
        Assert.AreEqual("UploadComplete", original.MessageType);
        AdapterControlFrame replay = await session.UploadAsync("left", "replay.bin", bytes, operation);
        Assert.AreEqual(original.Payload.GetProperty("revision").GetString(), replay.Payload.GetProperty("revision").GetString());
        byte[] changed = (byte[])bytes.Clone();
        changed[0] ^= 1;
        AdapterControlFrame mismatch = await session.UploadAsync("left", "replay.bin", changed, operation);
        Assert.AreEqual("OperationBindingMismatch", mismatch.Payload.GetProperty("code").GetString());
        AdapterControlFrame conflict = await session.UploadAsync("left", "same.txt", "replacement"u8.ToArray());
        Assert.AreEqual("RemoteConflict", conflict.Payload.GetProperty("code").GetString());
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(session.Root, "left", "replay.bin")));
    }

    [TestMethod]
    public async Task CancelCleansTheLeaseBeforeAcknowledgingAndAllowsAnotherUpload()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        Guid request = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        Guid stream = Guid.NewGuid();
        await session.SendAsync("Upload", request, new
        {
            rootKey = "left",
            path = "cancel.bin",
            operationId = operation,
            streamId = stream,
            length = 2,
            preconditions = new AdapterMutationPreconditions()
        });
        Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
        await session.SendChunkAsync(request, stream, "left", 0, [1], last: false);
        Guid cancel = Guid.NewGuid();
        await session.SendAsync("Cancel", cancel, new { rootKey = "left", targetRequestId = request, operationId = operation });
        AdapterControlFrame canceled = await session.ReadAsync();
        Assert.AreEqual(request, canceled.RequestId);
        Assert.AreEqual("Canceled", canceled.Payload.GetProperty("code").GetString());
        AdapterControlFrame ack = await session.ReadAsync();
        Assert.AreEqual(cancel, ack.RequestId);
        Assert.AreEqual("CancelAck", ack.MessageType);
        Assert.IsFalse(Directory.EnumerateFiles(session.Cache).Any());
        Assert.IsFalse(File.Exists(Path.Combine(session.Root, "left", "cancel.bin")));
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("right", "after-cancel.bin", [4, 5])).MessageType);
    }
}
