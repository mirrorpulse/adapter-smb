using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MirrorPulse.Adapter.Smb.Worker;

/// <summary>Confines SMB paths to the UNC directory authorized for one root.</summary>
public sealed class SmbWorkerPaths
{
    private readonly string _root;
    private readonly string _prefix;

    public SmbWorkerPaths(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        _root = NormalizeNetworkPath(sourceDirectory);
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(_root);
        }

        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A reparse-point source directory is not allowed.");
        }

        _prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    internal SafeAccessTokenHandle? Identity { get; set; }

    internal Task RunAsync(Func<Task> action) => Identity is null ? action() : WindowsIdentity.RunImpersonatedAsync(Identity, action);

    public static string NormalizeNetworkPath(string networkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkPath);
        if (networkPath.Length > 4096 || !networkPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            networkPath.StartsWith("\\\\?\\", StringComparison.Ordinal) || networkPath.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            networkPath.Contains('/') || networkPath.Any(char.IsControl))
            throw new InvalidDataException("InvalidNetworkPath");
        string[] parts = networkPath.TrimEnd('\\')[2..].Split('\\');
        if (parts.Length < 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("InvalidNetworkPath");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(networkPath));
    }

    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Length > 4096 || Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\0') || relativePath.StartsWith('\\') ||
            relativePath.StartsWith('/'))
        {
            throw new InvalidDataException("The SMB Worker path must be bounded and relative.");
        }

        string[] segments = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment) || IsTransferName(segment)))
        {
            throw new InvalidDataException("The SMB Worker path contains an unsafe segment.");
        }

        string path = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        if (!path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The SMB Worker path escaped the source directory.");
        }

        string current = _root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The SMB Worker cannot traverse a reparse point.");
            }
        }

        return path;
    }

    public string ResolveDirectory(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return _root;
        }

        return Resolve(relativePath);
    }

    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    internal static bool IsTransferName(string segment) => segment.StartsWith(".mp-upload-", StringComparison.OrdinalIgnoreCase) ||
        segment.StartsWith(".mp-recovery-", StringComparison.OrdinalIgnoreCase);
}
