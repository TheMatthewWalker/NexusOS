using System.Text.Json;

public sealed class NexusStatePaths
{
    public string Root { get; }

    public NexusStatePaths(IConfiguration configuration)
    {
        var explicitRoot = configuration["NEXUS_STATE_DIR"];
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var basePath = !string.IsNullOrWhiteSpace(explicitRoot)
            ? explicitRoot
            : !string.IsNullOrWhiteSpace(xdgState)
                ? Path.Combine(xdgState, "nexusos")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "nexusos");
        Root = Path.GetFullPath(basePath);
        RejectSymbolicLinkComponents(Root);
        Directory.CreateDirectory(Root);
        RejectSymbolicLinkComponents(Root);
        RestrictPermissions(Root, true);
    }

    // Reject symlinked ancestors as well as a symlink at the state directory
    // itself. This is defense in depth, not a race-free openat2 boundary.
    internal static void RejectSymbolicLinkComponents(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        while (!string.IsNullOrEmpty(path))
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Nexus state path cannot contain symbolic links.");
            }
            else if (new FileInfo(path).LinkTarget is not null)
                throw new IOException("Nexus state path cannot contain symbolic links.");
            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent == path) break;
            path = parent;
        }
    }

    public string PathFor(string name) => Path.Combine(Root, name);

    public static void RestrictPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
