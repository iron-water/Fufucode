using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Core;

/// <summary>Product-owned paths. Tests may supply an isolated root.</summary>
public sealed class AppDataLayout
{
    public AppDataLayout(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fufucode"));
    }

    public string Root { get; }
    public string RuntimeDirectory => Path.Combine(Root, "runtime");
    /// <summary>后端无关的代理配置目录（FUMO 与后端插件共用）。</summary>
    public string AgentDirectory => Path.Combine(Root, "agent");

    /// <summary>历史 Pi 代理目录：只读兼容，用于迁移前数据与旧配置。</summary>
    public string LegacyPiAgentDirectory => Path.Combine(Root, "pi-agent");
    public string SessionsDirectory => Path.Combine(Root, "sessions");
    public string DatabasePath => Path.Combine(Root, "desktop.db");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string ExportsDirectory => Path.Combine(Root, "exports");
    public string PluginsDirectory => Path.Combine(Root, "plugins");
    public string PluginDirectory(string pluginId) => Path.Combine(PluginsDirectory, pluginId);
    public string PluginStagingDirectory(string pluginId) => Path.Combine(PluginsDirectory, ".staging", pluginId);
    public string ProjectSessionsDirectory(string projectId) => Path.Combine(SessionsDirectory, projectId);
    public string UpdatesDirectory => Path.Combine(Root, "updates");
    public string UpdateStagingDirectory(string version) => Path.Combine(UpdatesDirectory, ".staging", version);
    public string UpdateBackupDirectory => Path.Combine(UpdatesDirectory, "backup");

    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, RuntimeDirectory, AgentDirectory,
            SessionsDirectory, LogsDirectory, ExportsDirectory, PluginsDirectory, UpdatesDirectory }) Directory.CreateDirectory(path);
    }

    public static string NormalizeProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        return full;
    }

    internal static string PathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    internal static bool IsUnderDirectory(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>An OS file-share lease prevents two Fufucode processes from writing one Pi session.</summary>
public sealed class SessionLease : IDisposable
{
    private readonly FileStream _stream;
    private SessionLease(FileStream stream) => _stream = stream;

    public static SessionLease Acquire(string sessionDirectory, string sessionId)
    {
        if (sessionId.Length == 0 || sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Invalid session ID.", nameof(sessionId));
        var lockDirectory = Path.Combine(sessionDirectory, ".locks");
        Directory.CreateDirectory(lockDirectory);
        // Hash the ID to avoid special Windows device names and case aliases in lock file names.
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToUpperInvariant())));
        try
        {
            return new SessionLease(new FileStream(Path.Combine(lockDirectory, name + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("This session is already owned by another process.", error);
        }
    }

    public void Dispose() => _stream.Dispose();
}
