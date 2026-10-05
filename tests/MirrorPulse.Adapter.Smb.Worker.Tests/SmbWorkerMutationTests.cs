using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker.Tests;

[TestClass]
[TestCategory("SmbNative")]
public sealed class SmbWorkerMutationTests
{
    [TestMethod]
    public async Task ConditionalReplacementUsesTheAcceptedRevisionAndRejectsAnOpenWriter()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string revision = await RevisionAsync(session, "left", "same.txt");
        AdapterControlFrame complete = await session.UploadAsync("left", "same.txt", "new-content"u8.ToArray(),
            preconditions: new(revision, DestinationMustBeAbsent: false));
        Assert.AreEqual("UploadComplete", complete.MessageType, complete.Payload.GetRawText());
        Assert.AreEqual("new-content", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        AdapterControlFrame stale = await session.UploadAsync("left", "same.txt", "stale"u8.ToArray(),
            preconditions: new(revision, DestinationMustBeAbsent: false));
        Assert.AreEqual("RemoteConflict", stale.Payload.GetProperty("code").GetString());
        string path = Path.Combine(session.Root, "right", "same.txt");
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            AdapterControlFrame blocked = await session.RequestAsync("Stat", new { rootKey = "right", path = "same.txt" });
            Assert.AreEqual("RemoteConflict", blocked.Payload.GetProperty("code").GetString());
        }
        Assert.AreEqual("right", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task CrossRootMoveIsRefusedAndSameRootRetryPreservesExistingDestinations()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string revision = await RevisionAsync(session, "left", "same.txt");
        var blocked = new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "right", "same.txt", new(revision));
        AdapterControlFrame conflict = await session.RequestAsync("Move", blocked);
        Assert.AreEqual("CrossRootMoveUnavailable", conflict.Payload.GetProperty("code").GetString());
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        Assert.AreEqual("right", await File.ReadAllTextAsync(Path.Combine(session.Root, "right", "same.txt")));
        var move = new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "moved.txt", new(revision));
        AdapterControlFrame accepted = await session.RequestAsync("Move", move);
        Assert.AreEqual("MutationComplete", accepted.MessageType, accepted.Payload.GetRawText());
        Assert.IsFalse(File.Exists(Path.Combine(session.Root, "left", "same.txt")));
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "moved.txt")));
        AdapterControlFrame retry = await session.RequestAsync("Move", move);
        Assert.AreEqual(accepted.Payload.GetProperty("revision").GetString(), retry.Payload.GetProperty("revision").GetString());
        AdapterControlFrame mismatch = await session.RequestAsync("Move", move with { DestinationPath = "different.txt" });
        Assert.AreEqual("OperationBindingMismatch", mismatch.Payload.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task DirectoryCreateAndEmptyDeleteRefuseUnprovenTreeMovesAndNewChildren()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        var create = new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder");
        AdapterControlFrame created = await session.RequestAsync("CreateDirectory", create);
        Assert.AreEqual("MutationComplete", created.MessageType, created.Payload.GetRawText());
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", create)).MessageType);
        Assert.AreEqual("DestinationExists", (await session.RequestAsync("CreateDirectory", create with { OperationId = Guid.NewGuid() })).Payload.GetProperty("code").GetString());
        var move = new AdapterOperationRequest(Guid.NewGuid(), "left", "folder", "left", "moved-folder",
            new(created.Payload.GetProperty("revision").GetString()), IsDirectory: true);
        AdapterControlFrame moved = await session.RequestAsync("Move", move);
        Assert.AreEqual("DirectoryMoveUnavailable", moved.Payload.GetProperty("code").GetString());
        Assert.IsTrue(Directory.Exists(Path.Combine(session.Root, "left", "folder")));
        Assert.IsFalse(Directory.Exists(Path.Combine(session.Root, "left", "moved-folder")));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory",
            new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "moved-folder"))).MessageType);
        await File.WriteAllTextAsync(Path.Combine(session.Root, "left", "moved-folder", "new-child.txt"), "preserve");
        string nonemptyRevision = await RevisionAsync(session, "left", "moved-folder");
        AdapterControlFrame nonempty = await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "moved-folder",
            Preconditions: new(nonemptyRevision), IsDirectory: true));
        Assert.AreEqual("DirectoryNotEmpty", nonempty.Payload.GetProperty("code").GetString());
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "moved-folder", "new-child.txt")));
        File.Delete(Path.Combine(session.Root, "left", "moved-folder", "new-child.txt"));
        string emptyRevision = await RevisionAsync(session, "left", "moved-folder");
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "moved-folder",
            Preconditions: new(emptyRevision), IsDirectory: true))).MessageType);
        Assert.IsFalse(Directory.Exists(Path.Combine(session.Root, "left", "moved-folder")));
    }

    [TestMethod]
    public async Task DestinationChangedDuringUploadIsPreservedAtTheFinalConditionCheck()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string revision = await RevisionAsync(session, "left", "same.txt");
        Guid request = Guid.NewGuid();
        Guid stream = Guid.NewGuid();
        await session.SendAsync("Upload", request, new
        {
            rootKey = "left",
            path = "same.txt",
            operationId = Guid.NewGuid(),
            streamId = stream,
            length = 4,
            preconditions = new AdapterMutationPreconditions(revision, DestinationMustBeAbsent: false)
        });
        Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
        await File.WriteAllTextAsync(Path.Combine(session.Root, "left", "same.txt"), "external-change");
        await session.SendChunkAsync(request, stream, "left", 0, "ours"u8.ToArray(), last: true);
        AdapterControlFrame conflict = await session.ReadAsync();
        Assert.AreEqual("RemoteConflict", conflict.Payload.GetProperty("code").GetString());
        Assert.AreEqual("external-change", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", "same.txt")));
        Assert.IsFalse(Directory.EnumerateFiles(session.Cache).Any());
    }

    [TestMethod]
    public async Task ParentSwappedForASymbolicLinkBeforeCommitCannotRedirectUpload()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string parent = Path.Combine(session.Root, "left", "folder");
        Directory.CreateDirectory(parent);
        Guid request = Guid.NewGuid();
        Guid stream = Guid.NewGuid();
        await session.SendAsync("Upload", request, new
        {
            rootKey = "left",
            path = "folder/redirected.txt",
            operationId = Guid.NewGuid(),
            streamId = stream,
            length = 4,
            preconditions = new AdapterMutationPreconditions()
        });
        Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
        Directory.Move(parent, parent + "-original");
        Directory.CreateSymbolicLink(parent, Path.Combine(session.Root, "right"));
        try
        {
            await session.SendChunkAsync(request, stream, "left", 0, "ours"u8.ToArray(), last: true);
            AdapterControlFrame rejected = await session.ReadAsync();
            Assert.AreEqual("OperationError", rejected.MessageType);
            Assert.IsFalse(File.Exists(Path.Combine(session.Root, "right", "redirected.txt")));
            Assert.IsFalse(Directory.EnumerateFiles(session.Cache).Any());
        }
        finally { Directory.Delete(parent); }
    }

    [TestMethod]
    public async Task StaleFileDeleteAndWindowsAliasNamesLeaveSourcesIntact()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        string revision = await RevisionAsync(session, "left", "same.txt");
        await File.WriteAllTextAsync(Path.Combine(session.Root, "left", "same.txt"), "changed");
        AdapterControlFrame stale = await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", Preconditions: new(revision)));
        Assert.AreEqual("RemoteConflict", stale.Payload.GetProperty("code").GetString());
        foreach (string name in new[] { "NUL", "CON.txt", "LPT1.log", "same.txt.", "same.txt " })
        {
            AdapterControlFrame alias = await session.RequestAsync("Stat", new { rootKey = "left", path = name });
            Assert.AreEqual("OperationError", alias.MessageType);
        }
        string current = await RevisionAsync(session, "left", "same.txt");
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", Preconditions: new(current)))).MessageType);
        Assert.IsFalse(File.Exists(Path.Combine(session.Root, "left", "same.txt")));
    }

    [TestMethod]
    public async Task InterruptedReplacementRetainsTheOriginalAndBlocksBlindReplay()
    {
        await using var session = await SmbWorkerSession.StartAsync();
        Guid operation = Guid.NewGuid();
        string recoveryName = ".mp-recovery-" + operation.ToString("N");
        string source = Path.Combine(session.Root, "left", "same.txt");
        File.Move(source, Path.Combine(session.Root, "left", recoveryName));
        await File.WriteAllTextAsync(source, "competing-new-content");
        AdapterControlFrame result = await session.UploadAsync("left", "same.txt", "ours"u8.ToArray(), operation);
        Assert.AreEqual("MutationOutcomeAmbiguous", result.Payload.GetProperty("code").GetString());
        Assert.AreEqual(recoveryName, result.Payload.GetProperty("recoveryRelativePath").GetString());
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Root, "left", recoveryName)));
        Assert.AreEqual("competing-new-content", await File.ReadAllTextAsync(source));
        AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
        Assert.IsFalse(page.Payload.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("relativePath").GetString() == recoveryName));
    }

    private static async Task<string> RevisionAsync(SmbWorkerSession session, string root, string path)
    {
        AdapterControlFrame stat = await session.RequestAsync("Stat", new { rootKey = root, path });
        Assert.AreEqual("StatResult", stat.MessageType, stat.Payload.GetRawText());
        return stat.Payload.GetProperty("revision").GetString()!;
    }
}
