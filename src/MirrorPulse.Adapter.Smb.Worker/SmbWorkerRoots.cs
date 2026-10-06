using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Smb.Worker;

/// <summary>Owns root-scoped outbound Windows identities; no drive mapping or saved password is created.</summary>
internal sealed class SmbWorkerRoots : IDisposable
{
    private readonly Dictionary<string, SmbWorkerPaths?> _roots = new(StringComparer.Ordinal);

    public static async Task<SmbWorkerRoots> CreateAsync(AdapterReady ready, AdapterControlChannel channel, CancellationToken token)
    {
        if (ready.Roots.Count is < 1 or > 64) throw new InvalidDataException("InvalidRoots");
        var roots = new SmbWorkerRoots();
        try
        {
            foreach (AdapterRootBinding binding in ready.Roots)
            {
                if (!binding.Enabled) { roots._roots.Add(binding.RootKey, null); continue; }
                string path = SmbWorkerPaths.NormalizeNetworkPath(binding.Configuration.GetValueOrDefault("networkPath") ??
                    throw new InvalidDataException("NetworkPathRequired"));
                SafeAccessTokenHandle? identity = null;
                try
                {
                    if (binding.Configuration.GetValueOrDefault("credentialReference") is { Length: > 0 } reference)
                    {
                        string username = binding.Configuration.GetValueOrDefault("username") ?? throw new InvalidDataException("UsernameRequired");
                        string? domain = binding.Configuration.GetValueOrDefault("domain");
                        ValidateIdentity(username, domain);
                        Guid request = Guid.NewGuid();
                        await channel.SendAsync("CredentialRequest", request, false,
                            new { rootKey = binding.RootKey, referenceId = reference }, token).ConfigureAwait(false);
                        AdapterControlFrame response = await channel.ReadAsync(token).ConfigureAwait(false);
                        if (response.MessageType != "CredentialResponse" || !response.IsResponse || response.RequestId != request ||
                            response.Payload.GetProperty("referenceId").GetString() != reference)
                            throw new InvalidDataException("InvalidCredentialResponse");
                        string password = response.Payload.GetProperty("secret").GetString() ?? throw new InvalidDataException("CredentialRequired");
                        if (password.Length is < 1 or > 4096 || password.Contains('\0')) throw new InvalidDataException("InvalidCredential");
                        // Remote servers use an independent outbound logon session. Local SMB
                        // loopback must also replace the local SID used by Windows authorization.
                        int logonType = await SmbServerIdentity.IsLocalAsync(path, token).ConfigureAwait(false) ? 8 : 9;
                        if (!LogonUserW(username, domain, password, logonType, 3, out identity))
                            throw new UnauthorizedAccessException("IdentityUnavailable");
                    }
                    SmbWorkerPaths paths = identity is null ? new(path) :
                        WindowsIdentity.RunImpersonated(identity, () => new SmbWorkerPaths(path));
                    paths.Identity = identity;
                    roots._roots.Add(binding.RootKey, paths);
                    identity = null;
                }
                finally { identity?.Dispose(); }
            }
            return roots;
        }
        catch { roots.Dispose(); throw; }
    }

    private static void ValidateIdentity(string username, string? domain)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl) ||
            domain is { Length: > 256 } || domain?.Any(char.IsControl) == true ||
            username.Contains('\\') || (username.Contains('@') && domain is { Length: > 0 }))
            throw new InvalidDataException("InvalidIdentity");
    }

    public SmbWorkerPaths GetPaths(string root) => !_roots.TryGetValue(root, out SmbWorkerPaths? paths)
        ? throw new InvalidDataException("UnknownRoot") : paths ?? throw new InvalidDataException("RootOffline");

    public void Dispose()
    {
        foreach (SmbWorkerPaths? paths in _roots.Values) paths?.Identity?.Dispose();
        _roots.Clear();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(string username, string? domain, string password, int logonType,
        int provider, out SafeAccessTokenHandle token);
}
