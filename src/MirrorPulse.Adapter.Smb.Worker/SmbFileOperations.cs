using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker;

/// <summary>Conditional operations retain ordinary Windows sharing locks through the native mutation.</summary>
internal static class SmbFileOperations
{
    public static async Task<string?> RevisionAsync(SmbWorkerPaths paths, string relative, CancellationToken token)
    {
        string path = paths.ResolveDirectory(relative);
        using var parents = new DirectoryLease(Path.GetDirectoryName(path) ?? paths.Root);
        using ProtectedObject? item = ProtectedObject.TryOpen(path, mutate: false);
        return item is null ? null : await item.RevisionAsync(token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(SmbWorkerPaths paths, string relative, long offset, int length,
        string? expected, CancellationToken token)
    {
        string path = paths.Resolve(relative);
        using var parents = new DirectoryLease(Path.GetDirectoryName(path)!);
        using ProtectedObject item = ProtectedObject.TryOpen(path, mutate: false) ?? throw new FileNotFoundException();
        if (item.IsDirectory) throw new InvalidDataException("ItemKindMismatch");
        if (expected is not null && expected != await item.RevisionAsync(token).ConfigureAwait(false)) throw new InvalidDataException("RemoteConflict");
        if (offset > item.Stream!.Length || length > item.Stream.Length - offset) throw new InvalidDataException("InvalidRange");
        item.Stream.Position = offset;
        byte[] result = new byte[length];
        await item.Stream.ReadExactlyAsync(result, token).ConfigureAwait(false);
        return result;
    }

    public static async Task<string?> UploadAsync(SmbWorkerPaths paths, AdapterOperationRequest operation, Stream content, CancellationToken token)
    {
        string destination = paths.Resolve(operation.Path);
        using var parents = new DirectoryLease(Path.GetDirectoryName(destination)!);
        EnsureNoRecovery(paths, operation);
        using ProtectedObject? existing = ProtectedObject.TryOpen(destination, mutate: true);
        if (existing?.IsDirectory == true) throw new InvalidDataException("ItemKindMismatch");
        AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
        string? actual = existing is null ? null : await existing.RevisionAsync(token).ConfigureAwait(false);
        if (actual != conditions.ExpectedRevision || (conditions.DestinationMustBeAbsent && existing is not null))
            throw new InvalidDataException("RemoteConflict");
        string staged = Path.Combine(Path.GetDirectoryName(destination)!, ".mp-upload-" + operation.OperationId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        bool accepted = false;
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                content.Position = 0;
                await content.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            using ProtectedObject candidate = ProtectedObject.TryOpen(staged, mutate: true) ?? throw new FileNotFoundException();
            string? recovery = null;
            if (existing is not null)
            {
                recovery = RecoveryPath(paths, operation);
                // Move the exact accepted object, retaining its sharing lock. The
                // publication below never overwrites a newly created destination.
                Native.Rename(existing.Handle, recovery);
                accepted = true;
            }
            try { Native.Rename(candidate.Handle, destination); }
            catch (NativeFileException)
            {
                if (recovery is not null)
                {
                    try { Native.Rename(existing!.Handle, destination); accepted = false; }
                    catch (NativeFileException) { throw RecoveryRequired(paths, recovery); }
                }
                throw;
            }
            accepted = true;
            string revision = await candidate.RevisionAsync(token).ConfigureAwait(false);
            if (recovery is not null)
            {
                try { Native.Delete(existing!.Handle); }
                catch (NativeFileException) { throw RecoveryRequired(paths, recovery); }
            }
            return revision;
        }
        catch (IOException exception) when (accepted && exception is not SmbRecoveryRequiredException)
        {
            throw new SmbMutationOutcomeAmbiguousException();
        }
        finally
        {
            try { File.Delete(staged); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A disconnected server can retain this hidden, operation-bound stage.
                // Cleanup must not mask an acceptance or recovery outcome already known.
            }
        }
    }

    public static async Task<string?> MutateAsync(string type, SmbWorkerPaths sourcePaths, AdapterOperationRequest operation,
        SmbWorkerPaths? destinationPaths, CancellationToken token)
    {
        string source = sourcePaths.Resolve(operation.Path);
        using var sourceParents = new DirectoryLease(Path.GetDirectoryName(source)!);
        using ProtectedObject? item = ProtectedObject.TryOpen(source, mutate: true);
        if (type == "CreateDirectory")
        {
            if (item is not null)
            {
                if (!item.IsDirectory || (operation.Preconditions?.DestinationMustBeAbsent ?? true)) throw new InvalidDataException("DestinationExists");
                return await item.RevisionAsync(token).ConfigureAwait(false);
            }
            Native.CreateDirectory(source);
            try
            {
                using ProtectedObject created = ProtectedObject.TryOpen(source, mutate: false) ?? throw new DirectoryNotFoundException();
                return await created.RevisionAsync(token).ConfigureAwait(false);
            }
            catch (IOException) { throw new SmbMutationOutcomeAmbiguousException(); }
        }
        if (item is null) throw new InvalidDataException("RemoteConflict");
        if (item.IsDirectory != operation.IsDirectory) throw new InvalidDataException("ItemKindMismatch");
        string? actual = await item.RevisionAsync(token).ConfigureAwait(false);
        if (actual != operation.Preconditions?.ExpectedRevision) throw new InvalidDataException("RemoteConflict");
        token.ThrowIfCancellationRequested();
        if (type == "Delete")
        {
            // Native disposition refuses nonempty directories; recursive blind
            // deletion cannot prove that every child was accepted by the Host.
            Native.Delete(item.Handle);
            return null;
        }
        if (type != "Move" || destinationPaths is null) throw new InvalidDataException("CapabilityUnavailable");
        string destination = destinationPaths.Resolve(operation.DestinationPath!);
        using var destinationParents = new DirectoryLease(Path.GetDirectoryName(destination)!);
        Native.Rename(item.Handle, destination);
        try { return await item.RevisionAsync(token).ConfigureAwait(false); }
        catch (IOException) { throw new SmbMutationOutcomeAmbiguousException(); }
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public DirectoryLease(string path)
        {
            string full = Path.GetFullPath(path);
            string current = Path.GetPathRoot(full) ?? throw new InvalidDataException("InvalidSource");
            try
            {
                Add(current);
                foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    Add(current);
                }
            }
            catch { Dispose(); throw; }
        }

        private void Add(string path)
        {
            SafeFileHandle handle = Native.Open(path, 0x80, FileShare.Read);
            try
            {
                Native.FileInformation information = Native.Information(handle);
                if ((information.Attributes & 0x400) != 0 || (information.Attributes & 0x10) == 0)
                    throw new InvalidDataException("ReparsePointForbidden");
                _handles.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (int index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
            _handles.Clear();
        }
    }

    private sealed class ProtectedObject : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly Native.FileInformation _information;

        private ProtectedObject(SafeFileHandle handle, Native.FileInformation information)
        {
            _handle = handle;
            _information = information;
            if (!IsDirectory) Stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }

        public SafeFileHandle Handle => _handle;
        public FileStream? Stream { get; }
        public bool IsDirectory => (_information.Attributes & 0x10) != 0;

        public static ProtectedObject? TryOpen(string path, bool mutate)
        {
            SafeFileHandle handle;
            try { handle = Native.Open(path, 0x80000000 | (mutate ? 0x10000u : 0), FileShare.Read); }
            catch (NativeFileException exception) when (exception.NativeError is 2 or 3) { return null; }
            try
            {
                Native.FileInformation information = Native.Information(handle);
                if ((information.Attributes & 0x400) != 0) throw new InvalidDataException("ReparsePointForbidden");
                return new(handle, information);
            }
            catch { handle.Dispose(); throw; }
        }

        public async Task<string> RevisionAsync(CancellationToken token)
        {
            string time = ((ulong)_information.WriteTime.High << 32 | _information.WriteTime.Low).ToString(CultureInfo.InvariantCulture);
            if (IsDirectory) return "directory:" + time;
            Stream!.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(Stream, token).ConfigureAwait(false));
            return Stream.Length.ToString(CultureInfo.InvariantCulture) + ":" + time + ":" + digest;
        }

        public void Dispose()
        {
            Stream?.Dispose();
            _handle.Dispose();
        }
    }

    private static class Native
    {
        public static SafeFileHandle Open(string path, uint access, FileShare share)
        {
            SafeFileHandle handle = CreateFileW(ExtendedPath(path), access, (uint)share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new NativeFileException(error);
        }

        public static FileInformation Information(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out FileInformation information)) throw new NativeFileException(Marshal.GetLastPInvokeError());
            return information;
        }

        public static void Rename(SafeFileHandle handle, string destination)
        {
            byte[] name = Encoding.Unicode.GetBytes(ExtendedPath(destination));
            int nameOffset = IntPtr.Size * 2 + 4;
            byte[] buffer = new byte[checked(nameOffset + name.Length)];
            // FILE_RENAME_INFO_EX layout is shared by x64 and ARM64.
            BitConverter.GetBytes(name.Length).CopyTo(buffer, IntPtr.Size * 2);
            name.CopyTo(buffer, nameOffset);
            if (!SetFileInformationByHandle(handle, 3, buffer, checked((uint)buffer.Length))) throw MutationFailure();
        }

        public static void Delete(SafeFileHandle handle)
        {
            if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw MutationFailure();
        }

        public static void CreateDirectory(string path)
        {
            if (!CreateDirectoryW(ExtendedPath(path), IntPtr.Zero)) throw MutationFailure();
        }

        private static string ExtendedPath(string path) => "\\\\?\\UNC\\" + Path.GetFullPath(path)[2..];

        private static IOException MutationFailure()
        {
            int error = Marshal.GetLastPInvokeError();
            // Known native refusals prove rejection. A transport or unknown error can
            // arrive after the server committed; never present it as safe to replay.
            return error is 1 or 2 or 3 or 5 or 17 or 32 or 33 or 50 or 80 or 87 or 145 or 183 or 206
                ? new NativeFileException(error) : new SmbMutationOutcomeAmbiguousException();
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime { public uint Low; public uint High; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            public uint Attributes;
            public FileTime CreationTime;
            public FileTime AccessTime;
            public FileTime WriteTime;
            public uint Volume;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);
    }

    public static void EnsureNoRecovery(SmbWorkerPaths paths, AdapterOperationRequest operation)
    {
        string recovery = RecoveryPath(paths, operation);
        if (File.Exists(recovery)) throw RecoveryRequired(paths, recovery);
    }

    private static string RecoveryPath(SmbWorkerPaths paths, AdapterOperationRequest operation) =>
        Path.Combine(Path.GetDirectoryName(paths.Resolve(operation.Path))!, ".mp-recovery-" + operation.OperationId.ToString("N"));

    private static SmbRecoveryRequiredException RecoveryRequired(SmbWorkerPaths paths, string recovery) =>
        new(Path.GetRelativePath(paths.Root, recovery).Replace(Path.DirectorySeparatorChar, '/'));
}

internal sealed class NativeFileException(int error) : IOException("SmbNativeFailure")
{
    public int NativeError { get; } = error;
}

internal sealed class SmbRecoveryRequiredException(string path) : IOException("MutationOutcomeAmbiguous")
{
    public string RecoveryRelativePath { get; } = path;
}

internal sealed class SmbMutationOutcomeAmbiguousException() : IOException("MutationOutcomeAmbiguous");
