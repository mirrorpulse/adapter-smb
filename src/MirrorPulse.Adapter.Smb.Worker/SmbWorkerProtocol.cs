using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker;

/// <summary>One reader multiplexes control and bounded SDK transfer frames for all authorized roots.</summary>
internal sealed class SmbWorkerProtocol(AdapterControlChannel channel, AdapterWorkerProcessArguments arguments,
    SmbWorkerRoots roots, string cache) : IAsyncDisposable
{
    private readonly Dictionary<Guid, PendingUpload> _uploads = [];
    private readonly Dictionary<Guid, AcceptedUpload> _accepted = [];
    private readonly Queue<Guid> _acceptedOrder = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            AdapterWorkerFrame frame = await channel.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Chunk is { } chunk)
            {
                if (!_uploads.TryGetValue(chunk.RequestId, out PendingUpload? pending)) throw new InvalidDataException("UnexpectedChunk");
                await pending.Paths.RunAsync(() => ReceiveAsync(chunk, cancellationToken)).ConfigureAwait(false);
                continue;
            }
            AdapterControlFrame command = frame.Control!;
            if (command.IsResponse) throw new InvalidDataException("UnexpectedResponse");
            if (command.MessageType == "Stop")
            {
                await ReplyAsync(command, "Stopped", new { }, cancellationToken).ConfigureAwait(false);
                return;
            }
            try
            {
                if (command.MessageType == "Cancel")
                {
                    await CancelAsync(command, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                AdapterFileAddress address = AdapterProtocolJson.ReadAddress(command.Payload, 2);
                SmbWorkerPaths paths = roots.GetPaths(address.RootKey);
                await paths.RunAsync(async () =>
                {
                    switch (command.MessageType)
                    {
                        case "Stat":
                            await ReplyAsync(command, "StatResult", new
                            {
                                rootKey = address.RootKey,
                                revision = await SmbFileOperations.RevisionAsync(paths, address.Path, cancellationToken).ConfigureAwait(false)
                            }, cancellationToken).ConfigureAwait(false);
                            break;
                        case "List": await ListAsync(command, address, paths, cancellationToken).ConfigureAwait(false); break;
                        case "ReadRange": await ReadAsync(command, address, paths, cancellationToken).ConfigureAwait(false); break;
                        case "Upload": await BeginUploadAsync(command, address, paths, cancellationToken).ConfigureAwait(false); break;
                        case "Move":
                        case "Delete":
                        case "CreateDirectory": await MutateAsync(command, address, paths, cancellationToken).ConfigureAwait(false); break;
                        default: throw new InvalidDataException("CapabilityUnavailable");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationFailure(exception))
            {
                await ErrorAsync(command, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ListAsync(AdapterControlFrame command, AdapterFileAddress address, SmbWorkerPaths paths, CancellationToken token)
    {
        int size = command.Payload.GetProperty("pageSize").GetInt32();
        if (size is < 1 or > 512) throw new InvalidDataException("InvalidPageSize");
        int offset = 0;
        if (command.Payload.TryGetProperty("cursor", out JsonElement cursor) && cursor.ValueKind == JsonValueKind.String)
        {
            string text = cursor.GetString()!;
            if (text.Length > 8192) throw new InvalidDataException("InvalidCursor");
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Split('\0');
            if (parts.Length != 3 || parts[0] != address.RootKey || parts[1] != address.Path ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                throw new InvalidDataException("InvalidCursor");
        }
        string directory = paths.ResolveDirectory(address.Path);
        string[] children = Directory.EnumerateFileSystemEntries(directory).Where(item => !SmbWorkerPaths.IsTransferName(Path.GetFileName(item))).Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item, StringComparer.Ordinal).Skip(offset).Take(size + 1).ToArray();
        var entries = new List<object>(size);
        foreach (string child in children.Take(size))
        {
            string relative = Path.GetRelativePath(paths.Root, child).Replace(Path.DirectorySeparatorChar, '/');
            string resolved = paths.Resolve(relative);
            bool isDirectory = Directory.Exists(resolved);
            entries.Add(new
            {
                remoteId = relative,
                relativePath = relative,
                remoteRevision = await SmbFileOperations.RevisionAsync(paths, relative, token).ConfigureAwait(false),
                itemKind = isDirectory ? "Directory" : "File",
                length = isDirectory ? (long?)null : new FileInfo(resolved).Length,
                creationTime = new DateTimeOffset(File.GetCreationTimeUtc(resolved), TimeSpan.Zero),
                lastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(resolved), TimeSpan.Zero),
                isDeleted = false
            });
        }
        bool complete = children.Length <= size;
        await ReplyAsync(command, "DirectoryPage", new
        {
            rootKey = address.RootKey,
            entries,
            isComplete = complete,
            cursor = complete ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(address.RootKey + "\0" + address.Path + "\0" +
                checked(offset + entries.Count).ToString(CultureInfo.InvariantCulture)))
        }, token).ConfigureAwait(false);
    }

    private async Task ReadAsync(AdapterControlFrame command, AdapterFileAddress address, SmbWorkerPaths paths, CancellationToken token)
    {
        long offset = command.Payload.GetProperty("offset").GetInt64();
        long length = command.Payload.GetProperty("length").GetInt64();
        if (offset < 0 || length is < 1 or > AdapterBinaryChunkV2Codec.MaximumChunkBytes) throw new InvalidDataException("InvalidRange");
        string? expected = command.Payload.TryGetProperty("expectedRevision", out JsonElement revision) && revision.ValueKind == JsonValueKind.String ? revision.GetString() : null;
        byte[] bytes = await SmbFileOperations.ReadAsync(paths, address.Path, offset, checked((int)length), expected, token).ConfigureAwait(false);
        Guid stream = Guid.NewGuid();
        await ReplyAsync(command, "ReadRangeReady", new { rootKey = address.RootKey, streamId = stream, length }, token).ConfigureAwait(false);
        await channel.SendChunkAsync(new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, offset, bytes, true)
        { RootKey = address.RootKey }, token).ConfigureAwait(false);
    }

    private async Task BeginUploadAsync(AdapterControlFrame command, AdapterFileAddress address, SmbWorkerPaths paths, CancellationToken token)
    {
        AdapterOperationRequest operation = DecodeOperation(command);
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: false);
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid stream = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || stream == Guid.Empty || _uploads.Count >= 4) throw new InvalidDataException("UploadLimit");
        if (_uploads.Values.Any(pending => pending.Operation.OperationId == operation.OperationId)) throw new InvalidDataException("OperationInProgress");
        string destination = paths.Resolve(address.Path);
        SmbFileOperations.EnsureNoRecovery(paths, operation);
        string fingerprint = Fingerprint(command.MessageType, operation, length);
        bool replay = _accepted.TryGetValue(operation.OperationId, out AcceptedUpload? accepted);
        if (replay && accepted!.Fingerprint != fingerprint) throw new InvalidDataException("OperationBindingMismatch");
        if (!replay)
        {
            string? current = await SmbFileOperations.RevisionAsync(paths, address.Path, token).ConfigureAwait(false);
            AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
            if (current != conditions.ExpectedRevision || (conditions.DestinationMustBeAbsent && current is not null))
                throw new InvalidDataException("RemoteConflict");
        }
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new InvalidDataException("ParentNotFound");
        var lease = WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () => new AdapterTransferLease(cache));
        try
        {
            _uploads.Add(command.RequestId, new(command, operation, paths, fingerprint, replay,
                new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, address.RootKey, 0, length), lease));
        }
        catch { await DisposeLeaseAsync(lease).ConfigureAwait(false); throw; }
        await ReplyAsync(command, "UploadReady", new { rootKey = address.RootKey, operationId = operation.OperationId, streamId = stream }, token).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(AdapterBinaryChunk chunk, CancellationToken token)
    {
        if (!_uploads.TryGetValue(chunk.RequestId, out PendingUpload? upload)) throw new InvalidDataException("UnexpectedChunk");
        try
        {
            upload.Binding.Accept(chunk);
            await upload.Lease.Stream.WriteAsync(chunk.Data, token).ConfigureAwait(false);
            if (!upload.Binding.Completed) return;
            upload.Lease.Stream.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(upload.Lease.Stream, token).ConfigureAwait(false));
            string? revision;
            if (upload.Replay)
            {
                AcceptedUpload accepted = _accepted[upload.Operation.OperationId];
                if (accepted.Digest != digest) throw new InvalidDataException("OperationBindingMismatch");
                revision = accepted.Revision;
            }
            else
            {
                revision = await SmbFileOperations.UploadAsync(upload.Paths, upload.Operation, upload.Lease.Stream, token).ConfigureAwait(false);
                _accepted.Add(upload.Operation.OperationId, new(upload.Fingerprint, digest, revision));
                _acceptedOrder.Enqueue(upload.Operation.OperationId);
                if (_acceptedOrder.Count > 256) _accepted.Remove(_acceptedOrder.Dequeue());
            }
            await DisposeLeaseAsync(upload.Lease).ConfigureAwait(false);
            await ReplyAsync(upload.Command, "UploadComplete", new
            {
                rootKey = upload.Operation.RootKey,
                operationId = upload.Operation.OperationId,
                revision
            }, token).ConfigureAwait(false);
            _uploads.Remove(chunk.RequestId);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            _uploads.Remove(chunk.RequestId);
            await DisposeLeaseAsync(upload.Lease).ConfigureAwait(false);
            await ErrorAsync(upload.Command, exception, token).ConfigureAwait(false);
        }
    }

    private async Task CancelAsync(AdapterControlFrame cancel, CancellationToken token)
    {
        string root = cancel.Payload.GetProperty("rootKey").GetString() ?? throw new InvalidDataException("RootRequired");
        roots.GetPaths(root);
        Guid target = cancel.Payload.GetProperty("targetRequestId").GetGuid();
        string status = "alreadyCompleted";
        if (_uploads.TryGetValue(target, out PendingUpload? upload))
        {
            if (upload.Operation.RootKey != root) throw new InvalidDataException("CancelRootMismatch");
            if (cancel.Payload.TryGetProperty("operationId", out JsonElement operation) && operation.ValueKind != JsonValueKind.Null &&
                operation.GetGuid() != upload.Operation.OperationId) throw new InvalidDataException("CancelOperationMismatch");
            _uploads.Remove(target);
            await WindowsIdentity.RunImpersonatedAsync(SafeAccessTokenHandle.InvalidHandle, () => upload.Lease.CancelAsync().AsTask()).ConfigureAwait(false);
            await ErrorAsync(upload.Command, new InvalidDataException("Canceled"), token).ConfigureAwait(false);
            status = "canceled";
        }
        await ReplyAsync(cancel, "CancelAck", new { rootKey = root, targetRequestId = target, status }, token).ConfigureAwait(false);
    }

    private static AdapterOperationRequest DecodeOperation(AdapterControlFrame command) =>
        AdapterProtocolJson.Decode<AdapterOperationRequest>(Encoding.UTF8.GetBytes(command.Payload.GetRawText()));

    private async Task MutateAsync(AdapterControlFrame command, AdapterFileAddress address, SmbWorkerPaths paths, CancellationToken token)
    {
        AdapterOperationRequest operation;
        if (command.MessageType == "CreateDirectory")
        {
            AdapterCreateDirectoryRequest create = AdapterProtocolJson.Decode<AdapterCreateDirectoryRequest>(Encoding.UTF8.GetBytes(command.Payload.GetRawText()));
            operation = new(create.OperationId, create.RootKey, create.Path, Preconditions: new(null, create.MustBeAbsent), IsDirectory: true);
        }
        else operation = DecodeOperation(command);
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: command.MessageType == "Move");
        if (address.Path.Length == 0 || operation.DestinationPath?.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        // A rename cannot transfer authentication or prove an atomic commit across SMB shares.
        if (command.MessageType == "Move" && operation.DestinationRootKey != operation.RootKey)
            throw new InvalidDataException("CrossRootMoveUnavailable");
        SmbWorkerPaths? destination = command.MessageType == "Move" ? roots.GetPaths(operation.DestinationRootKey!) : null;
        string fingerprint = Fingerprint(command.MessageType, operation, null);
        string? revision;
        if (_accepted.TryGetValue(operation.OperationId, out AcceptedUpload? accepted))
        {
            if (accepted.Fingerprint != fingerprint) throw new InvalidDataException("OperationBindingMismatch");
            revision = accepted.Revision;
        }
        else
        {
            revision = await SmbFileOperations.MutateAsync(command.MessageType, paths, operation, destination, token).ConfigureAwait(false);
            _accepted.Add(operation.OperationId, new(fingerprint, null, revision));
            _acceptedOrder.Enqueue(operation.OperationId);
            if (_acceptedOrder.Count > 256) _accepted.Remove(_acceptedOrder.Dequeue());
        }
        await ReplyAsync(command, "MutationComplete", new { rootKey = operation.RootKey, operationId = operation.OperationId, revision }, token).ConfigureAwait(false);
    }

    private static string Fingerprint(string type, AdapterOperationRequest operation, long? length) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new { type, operation, length })));

    private ValueTask ReplyAsync(AdapterControlFrame command, string type, object payload, CancellationToken token) =>
        channel.SendAsync(type, command.RequestId, true, payload, token);

    private static bool IsOperationFailure(Exception exception) => exception is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or FormatException or KeyNotFoundException;

    private ValueTask ErrorAsync(AdapterControlFrame command, Exception exception, CancellationToken token)
    {
        string code = exception switch
        {
            SmbRecoveryRequiredException => "MutationOutcomeAmbiguous",
            NativeFileException { NativeError: 32 or 33 } => "RemoteConflict",
            NativeFileException { NativeError: 80 or 183 } => "DestinationExists",
            NativeFileException { NativeError: 145 } => "DirectoryNotEmpty",
            NativeFileException { NativeError: 1 or 17 or 50 or 87 } => "CapabilityUnavailable",
            NativeFileException { NativeError: 5 } => "AccessDenied",
            InvalidDataException => exception.Message,
            UnauthorizedAccessException => "AccessDenied",
            FileNotFoundException or DirectoryNotFoundException => "SourceUnavailable",
            IOException => "RetryableTransferFailure",
            _ => "InvalidRequest"
        };
        JsonElement payload = command.Payload;
        return ReplyAsync(command, "OperationError", new
        {
            code,
            recoveryRelativePath = (exception as SmbRecoveryRequiredException)?.RecoveryRelativePath,
            rootKey = payload.TryGetProperty("rootKey", out JsonElement root) && root.ValueKind == JsonValueKind.String ? root.GetString() : null,
            operationId = payload.TryGetProperty("operationId", out JsonElement operation) && operation.ValueKind == JsonValueKind.String && operation.TryGetGuid(out Guid id) ? (Guid?)id : null
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (PendingUpload upload in _uploads.Values) await DisposeLeaseAsync(upload.Lease).ConfigureAwait(false);
        _uploads.Clear();
    }

    private static Task DisposeLeaseAsync(AdapterTransferLease lease) =>
        WindowsIdentity.RunImpersonatedAsync(SafeAccessTokenHandle.InvalidHandle, () => lease.DisposeAsync().AsTask());

    private sealed record AcceptedUpload(string Fingerprint, string? Digest, string? Revision);
    private sealed record PendingUpload(AdapterControlFrame Command, AdapterOperationRequest Operation, SmbWorkerPaths Paths,
        string Fingerprint, bool Replay, AdapterStreamBinding Binding, AdapterTransferLease Lease);
}
