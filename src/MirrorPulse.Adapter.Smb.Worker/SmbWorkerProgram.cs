using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker;

public static class SmbWorkerProgram
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static async Task<int> RunAsync(IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        AdapterWorkerProcessArguments arguments;
        try { arguments = AdapterWorkerProcessArguments.Parse(args); }
        catch (ArgumentException) { return 2; }
        await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(
            arguments.PipeName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var channel = new AdapterControlChannel(pipe, arguments.InstanceId, arguments.WorkerSessionId);
        Guid helloId = Guid.NewGuid();
        await channel.SendAsync("Hello", helloId, false, new
        {
            adapterId = "com.mirrorpulse.adapter.smb",
            minimumProtocolVersion = 1,
            maximumProtocolVersion = 1,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            AdapterControlFrame ready = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (ready.MessageType != "Ready" || !ready.IsResponse || ready.RequestId != helloId)
                throw new InvalidDataException("The Host did not accept the SMB Worker handshake.");
            Dictionary<string, string> config = ready.Payload.Deserialize<Dictionary<string, string>>(Options)
                ?? throw new InvalidDataException("The SMB configuration is missing.");
            string root = config.GetValueOrDefault("networkPath")
                ?? throw new InvalidDataException("The SMB networkPath is missing.");
            if (!root.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(root))
                throw new DirectoryNotFoundException(root);
            var paths = new SmbWorkerPaths(root);
            await channel.SendAsync("Connected", helloId, false, new { networkPath = root }, cancellationToken);
            var protocol = new SmbTransferProtocol(channel, paths, arguments.InstanceId, arguments.WorkerSessionId);
            while (true)
            {
                AdapterControlFrame command = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (command.IsResponse) throw new InvalidDataException("The Host sent an unexpected response.");
                if (command.MessageType == "Stop")
                {
                    await channel.SendAsync("Stopped", command.RequestId, true, new { }, cancellationToken);
                    return 0;
                }
                await protocol.HandleAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
        catch (Exception exception)
        {
            string code = exception switch
            {
                InvalidDataException or JsonException => "InvalidConfiguration",
                UnauthorizedAccessException => "AccessDenied",
                DirectoryNotFoundException or FileNotFoundException => "SourceUnavailable",
                IOException => "NetworkUnavailable",
                _ => "ConnectionFailed",
            };
            await channel.SendAsync("Error", helloId, false, new { code }, CancellationToken.None);
            return 1;
        }
    }
}

internal sealed class SmbWorkerPaths
{
    private readonly string _root;
    private readonly string _prefix;

    public SmbWorkerPaths(string root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _prefix = _root;
    }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
            throw new InvalidDataException("The SMB path must be relative.");
        string[] parts = relativePath.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new InvalidDataException("The SMB path contains an unsafe segment.");
        string path = Path.GetFullPath(Path.Combine(_root, Path.Combine(parts)));
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The SMB path escaped the configured share.");
        return path;
    }

    public string Root => _root.TrimEnd(Path.DirectorySeparatorChar);

    public string ResolveDirectory(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? Root : Resolve(relativePath);
}

internal sealed class SmbTransferProtocol(AdapterControlChannel channel, SmbWorkerPaths paths,
    Guid instanceId, Guid sessionId)
{
    private const int MaximumRangeBytes = 1024 * 1024;

    public async Task HandleAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.MessageType)
            {
                case "Stat": await StatAsync(command, cancellationToken); break;
                case "ReadRange": await ReadRangeAsync(command, cancellationToken); break;
                case "Upload": await UploadAsync(command, cancellationToken); break;
                case "List": await ListAsync(command, cancellationToken); break;
                default: throw new InvalidDataException("The SMB Worker received an unsupported command.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception is SmbRevisionConflictException ? "RemoteConflict" :
                exception is InvalidDataException or ArgumentException or JsonException ? "InvalidRequest" :
                exception is UnauthorizedAccessException ? "AccessDenied" : "RetryableTransferFailure";
            await channel.SendAsync("OperationError", command.RequestId, true, new { code }, CancellationToken.None);
        }
    }

    private async Task StatAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString() ?? throw new InvalidDataException("Path missing.");
        string file = paths.Resolve(path);
        if (!File.Exists(file))
        {
            await channel.SendAsync("StatResult", command.RequestId, true, new { revision = (string?)null }, cancellationToken);
            return;
        }
        await channel.SendAsync("StatResult", command.RequestId, true,
            new { revision = Revision(file), length = new FileInfo(file).Length }, cancellationToken);
    }

    private async Task ListAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString() ?? string.Empty;
        int pageSize = command.Payload.GetProperty("pageSize").GetInt32();
        if (pageSize is < 1 or > 512) throw new InvalidDataException("Page size invalid.");
        int offset = ParseCursor(command.Payload);
        string directory = paths.ResolveDirectory(path);
        string[] children = Directory.EnumerateFileSystemEntries(directory)
            .OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => Path.GetFileName(item), StringComparer.Ordinal)
            .ToArray();
        if (offset > children.Length) throw new InvalidDataException("Cursor is past the directory.");
        var entries = new List<object>(Math.Min(pageSize, children.Length - offset));
        foreach (string child in children.Skip(offset).Take(pageSize))
        {
            bool isDirectory = Directory.Exists(child);
            FileInfo? file = isDirectory ? null : new FileInfo(child);
            string relative = Path.GetRelativePath(paths.Root, child).Replace(Path.DirectorySeparatorChar, '/');
            DateTime creation = File.GetCreationTimeUtc(child);
            DateTime lastWrite = File.GetLastWriteTimeUtc(child);
            entries.Add(new
            {
                remoteId = relative,
                remoteRevision = isDirectory ? lastWrite.Ticks.ToString(CultureInfo.InvariantCulture) : Revision(child),
                itemKind = isDirectory ? "Directory" : "File",
                relativePath = relative,
                length = file?.Length,
                creationTime = new DateTimeOffset(creation, TimeSpan.Zero),
                lastWriteTime = new DateTimeOffset(lastWrite, TimeSpan.Zero),
                isDeleted = false,
            });
        }

        int next = offset + entries.Count;
        bool complete = next >= children.Length;
        await channel.SendAsync("DirectoryPage", command.RequestId, true, new
        {
            entries,
            cursor = complete ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                next.ToString(CultureInfo.InvariantCulture))),
            isComplete = complete,
        }, cancellationToken);
    }

    private static int ParseCursor(JsonElement payload)
    {
        if (!payload.TryGetProperty("cursor", out JsonElement cursor) ||
            cursor.ValueKind is JsonValueKind.Null || string.IsNullOrEmpty(cursor.GetString())) return 0;
        string text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor.GetString()!));
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int offset) && offset >= 0
            ? offset : throw new InvalidDataException("Cursor invalid.");
    }

    private async Task ReadRangeAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString() ?? throw new InvalidDataException("Path missing.");
        long offset = command.Payload.GetProperty("offset").GetInt64();
        int length = command.Payload.GetProperty("length").GetInt32();
        if (offset < 0 || length is < 0 or > MaximumRangeBytes) throw new InvalidDataException("Range invalid.");
        await using var input = new FileStream(paths.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        if (offset > input.Length || length > input.Length - offset) throw new InvalidDataException("Range exceeds file.");
        input.Position = offset;
        byte[] bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        Guid streamId = Guid.NewGuid();
        await channel.SendAsync("ReadRangeReady", command.RequestId, true, new { streamId, length }, cancellationToken);
        await channel.SendChunkAsync(new AdapterBinaryChunk(command.RequestId, instanceId, sessionId,
            streamId, offset, bytes, true), cancellationToken);
    }

    private async Task UploadAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString() ?? throw new InvalidDataException("Path missing.");
        string? expected = command.Payload.GetProperty("expectedRevision").GetString();
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid streamId = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || streamId == Guid.Empty) throw new InvalidDataException("Upload metadata invalid.");
        string cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR")
            ?? throw new InvalidDataException("Transfer cache missing.");
        Directory.CreateDirectory(cache);
        string staged = Path.Combine(cache, $"smb-{command.RequestId:N}.tmp");
        await channel.SendAsync("UploadReady", command.RequestId, true, new { streamId }, cancellationToken);
        try
        {
            await using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                long received = 0;
                while (true)
                {
                    AdapterBinaryChunk chunk = await channel.ReadChunkAsync(cancellationToken);
                    if (chunk.RequestId != command.RequestId || chunk.StreamId != streamId || chunk.Offset != received || chunk.Data.Length > length - received)
                        throw new InvalidDataException("Upload chunk invalid.");
                    await output.WriteAsync(chunk.Data, cancellationToken); received += chunk.Data.Length;
                    if (chunk.EndOfStream) { if (received != length) throw new InvalidDataException("Upload truncated."); break; }
                }
            }
            string destination = paths.Resolve(path);
            string? current = File.Exists(destination) ? Revision(destination) : null;
            if (!string.Equals(expected, current, StringComparison.Ordinal)) throw new SmbRevisionConflictException();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(staged, destination, true);
            await channel.SendAsync("UploadComplete", command.RequestId, true,
                new { revision = Revision(destination) }, cancellationToken);
        }
        finally { File.Delete(staged); }
    }

    private static string Revision(string path)
    {
        FileInfo file = new(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))[..8])}";
    }
}

internal sealed class SmbRevisionConflictException : IOException;
