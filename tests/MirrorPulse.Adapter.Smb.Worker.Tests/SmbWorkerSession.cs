using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker.Tests;

internal sealed class SmbWorkerSession : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private readonly Guid _instance = Guid.NewGuid();
    private readonly Guid _session = Guid.NewGuid();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
    private int _protocol = 1;

    private SmbWorkerSession(string root, bool privatePayload)
    {
        Root = root;
        Directory.CreateDirectory(root);
        string pipeName = "mp-smb-test-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string? configuredWorker = Environment.GetEnvironmentVariable("MP_SMB_TEST_WORKER_EXE");
        string executable = configuredWorker ?? Path.Combine(FindRepository(), "src", "MirrorPulse.Adapter.Smb.Worker", "bin", "Release",
            "net10.0-windows", "MirrorPulse.Adapter.Smb.Worker.exe");
        if (privatePayload)
        {
            string privateDirectory = Path.Combine(root, "private-worker");
            var directory = Directory.CreateDirectory(privateDirectory);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier identity in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!))
                File.Copy(file, Path.Combine(privateDirectory, Path.GetFileName(file)));
            executable = Path.Combine(privateDirectory, Path.GetFileName(executable));
        }
        Executable = executable;
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment.Clear();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd(Path.DirectorySeparatorChar);
        start.Environment["PATH"] = Environment.SystemDirectory;
        start.Environment["TEMP"] = root;
        start.Environment["TMP"] = root;
        foreach (string argument in new[] { "--instance-id", _instance.ToString("D"), "--worker-session-id", _session.ToString("D"), "--pipe-name", pipeName })
            start.ArgumentList.Add(argument);
        Cache = Path.Combine(root, "transfers");
        start.Environment["MP_TRANSFER_CACHE_DIR"] = Cache;
        if (configuredWorker is not null)
        {
            string unavailable = Path.Combine(root, "unavailable-runtime");
            Directory.CreateDirectory(unavailable);
            foreach (string name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" }) start.Environment[name] = unavailable;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            start.Environment["PATH"] = Environment.SystemDirectory;
        }
        _process = Process.Start(start) ?? throw new InvalidOperationException("Worker launch failed.");
    }

    public string Root { get; }
    public string Cache { get; }
    public string Executable { get; }
    public List<string> CredentialRoots { get; } = [];
    public AdapterControlFrame? StartupFrame { get; private set; }

    public static async Task<SmbWorkerSession> StartAsync(bool currentIdentity = false, bool wrongShare = false, bool shareRoot = false, bool privatePayload = false)
    {
        string backing = FixtureValue("BACKING");
        string directory = "mp-smb-v2-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(backing, directory);
        foreach (string name in new[] { "left", "right" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            await File.WriteAllTextAsync(Path.Combine(root, name, "same.txt"), name);
            await File.WriteAllTextAsync(Path.Combine(root, name, "second.txt"), "second-" + name);
        }
        var session = new SmbWorkerSession(root, privatePayload);
        try
        {
            await session._pipe.WaitForConnectionAsync(session._deadline.Token);
            AdapterControlFrame hello = await session.ReadAsync();
            Assert.AreEqual("Hello", hello.MessageType);
            Assert.AreEqual(1, hello.ProtocolVersion);
            Assert.AreEqual(2, AdapterHandshake.Negotiate(AdapterHandshake.ReadHello(hello.Payload), 3).SelectedVersion);
            Dictionary<string, string> RootConfiguration(string key)
            {
                string share = wrongShare && key == "right" ? "LEFT" : key.ToUpperInvariant();
                var values = new Dictionary<string, string>
                {
                    ["networkPath"] = shareRoot ? FixtureValue(share + "_SHARE") :
                    Path.Combine(FixtureValue(share + "_SHARE"), directory, key)
                };
                if (!currentIdentity)
                {
                    values["username"] = FixtureValue(key.ToUpperInvariant() + "_USER");
                    values["domain"] = Environment.MachineName;
                    values["credentialReference"] = "smb-" + key;
                }
                return values;
            }
            AdapterRootBinding[] roots = [new("left", true, RootConfiguration("left")),
                new("right", true, RootConfiguration("right")),
                new("offline", false, new Dictionary<string, string> { ["networkPath"] = "invalid-path",
                    ["username"] = "invalid\0identity", ["credentialReference"] = "must-not-be-requested" })];
            await session.SendAsync("Ready", hello.RequestId, new AdapterReady(2, AdapterHandshake.V2Capabilities, roots, new Dictionary<string, string>()), response: true);
            session._protocol = 2;
            AdapterControlFrame connected;
            while ((connected = await session.ReadAsync()).MessageType == "CredentialRequest")
            {
                string key = connected.Payload.GetProperty("rootKey").GetString()!;
                Assert.IsTrue(key is "left" or "right");
                Assert.DoesNotContain(key, session.CredentialRoots);
                string reference = connected.Payload.GetProperty("referenceId").GetString()!;
                Assert.AreEqual("smb-" + key, reference);
                session.CredentialRoots.Add(key);
                await session.SendAsync("CredentialResponse", connected.RequestId,
                    new { referenceId = reference, secret = FixtureValue(key.ToUpperInvariant() + "_PASSWORD") }, response: true);
            }
            session.StartupFrame = connected;
            if (wrongShare) Assert.IsTrue(connected.MessageType is "Error" or "Connected");
            else Assert.AreEqual("Connected", connected.MessageType);
            Assert.AreEqual(2, connected.ProtocolVersion);
            Assert.IsFalse(connected.Payload.TryGetProperty("networkPath", out _));
            if (Environment.GetEnvironmentVariable("MP_SMB_TEST_WORKER_EXE") is not null)
            {
                string privateRuntime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(session.Executable)!, "coreclr.dll"));
                ProcessModule[] modules = session._process.Modules.Cast<ProcessModule>().ToArray();
                Assert.AreEqual(privateRuntime, modules.Single(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)).FileName, ignoreCase: true);
            }
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private static string FixtureValue(string name) => Environment.GetEnvironmentVariable("MP_SMB_FIXTURE_" + name) ??
        throw new InvalidOperationException("A disposable SMB fixture is required; run the native CI gate.");

    public async Task<AdapterControlFrame> RequestAsync(string type, object payload)
    {
        Guid request = Guid.NewGuid();
        await SendAsync(type, request, payload);
        AdapterControlFrame result = await ReadAsync();
        Assert.AreEqual(request, result.RequestId);
        Assert.IsTrue(result.IsResponse);
        return result;
    }

    public Task SendAsync(string type, Guid request, object payload, bool response = false) => WriteFrameAsync(AdapterProtocolJson.Encode(
        new AdapterControlFrame(_protocol, type, request, _instance, _session, response, AdapterProtocolJson.ToElement(payload))));

    public Task SendChunkAsync(Guid request, Guid stream, string root, long offset, byte[] bytes, bool last) => WriteFrameAsync(
        AdapterBinaryChunkV2Codec.Encode(new(request, _instance, _session, stream, offset, bytes, last) { RootKey = root }));

    public async Task<AdapterControlFrame> ReadAsync()
    {
        AdapterControlFrame result = AdapterProtocolJson.Decode<AdapterControlFrame>(await ReadFrameAsync());
        Assert.AreEqual(_protocol, result.ProtocolVersion);
        Assert.AreEqual(_instance, result.InstanceId);
        Assert.AreEqual(_session, result.WorkerSessionId);
        return result;
    }

    public async Task<byte[]> ReadRangeAsync(string root, string path, int length)
    {
        AdapterControlFrame ready = await RequestAsync("ReadRange", new { rootKey = root, path, offset = 0, length });
        Assert.AreEqual("ReadRangeReady", ready.MessageType);
        Assert.AreEqual(root, ready.Payload.GetProperty("rootKey").GetString());
        AdapterBinaryChunk chunk = AdapterBinaryChunkV2Codec.Decode(await ReadFrameAsync());
        var binding = new AdapterStreamBinding(ready.RequestId, _instance, _session, ready.Payload.GetProperty("streamId").GetGuid(), root, 0, length);
        binding.Accept(chunk);
        Assert.IsTrue(binding.Completed);
        return chunk.Data.ToArray();
    }

    public async Task<AdapterControlFrame> UploadAsync(string root, string path, byte[] content, Guid? operation = null,
        AdapterMutationPreconditions? preconditions = null)
    {
        Guid request = Guid.NewGuid();
        Guid stream = Guid.NewGuid();
        await SendAsync("Upload", request, new
        {
            rootKey = root,
            path,
            operationId = operation ?? Guid.NewGuid(),
            streamId = stream,
            length = content.Length,
            preconditions = preconditions ?? new AdapterMutationPreconditions()
        });
        AdapterControlFrame ready = await ReadAsync();
        if (ready.MessageType == "OperationError") return ready;
        Assert.AreEqual("UploadReady", ready.MessageType);
        int offset = 0;
        do
        {
            int count = Math.Min(AdapterBinaryChunkV2Codec.MaximumChunkBytes, content.Length - offset);
            await SendChunkAsync(request, stream, root, offset, content.AsSpan(offset, count).ToArray(), offset + count == content.Length);
            offset += count;
        } while (offset < content.Length);
        AdapterControlFrame complete = await ReadAsync();
        Assert.AreEqual(request, complete.RequestId);
        return complete;
    }

    private async Task WriteFrameAsync(byte[] bytes)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)bytes.Length));
        await _pipe.WriteAsync(prefix, _deadline.Token);
        await _pipe.WriteAsync(bytes, _deadline.Token);
        await _pipe.FlushAsync(_deadline.Token);
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        byte[] prefix = new byte[4];
        await _pipe.ReadExactlyAsync(prefix, _deadline.Token);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        Assert.IsTrue(length is > 0 and <= 2 * 1024 * 1024);
        byte[] payload = new byte[checked((int)length)];
        await _pipe.ReadExactlyAsync(payload, _deadline.Token);
        return payload;
    }

    private static string FindRepository()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "eng", "adapter-sdk.lock.json")))
            directory = Directory.GetParent(directory)?.FullName;
        return directory ?? throw new DirectoryNotFoundException("Repository not found.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        _process.Dispose();
        await _pipe.DisposeAsync();
        _deadline.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
