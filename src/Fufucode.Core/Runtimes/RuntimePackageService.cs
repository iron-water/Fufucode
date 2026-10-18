using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fufucode.Core.Runtimes;

/// <summary>一个后端运行时分发包的安装状态。</summary>
public sealed record RuntimePackageStatus(
    string Id,
    bool Installed,
    string? Directory,
    long SizeBytes,
    string? Version,
    bool NodeIncluded);

/// <summary>
/// 可选后端运行时的安装/校验/移除。主安装包不携带 Node/Pi 运行时；
/// 用户可以从本机 zip 或 GitHub Release URL 安装到
/// &lt;dataRoot&gt;/runtime/&lt;id&gt;，校验 SHA-256 后原子替换。
/// </summary>
public sealed class RuntimePackageService
{
    private readonly AppDataLayout _layout;
    private readonly HttpClient _http;

    public RuntimePackageService(AppDataLayout layout, HttpClient? http = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public string Root(string id)
    {
        ValidateId(id);
        return Path.Combine(_layout.RuntimeDirectory, id);
    }

    public RuntimePackageStatus GetStatus(string id)
    {
        var directory = Root(id);
        var manifestPath = Path.Combine(directory, "runtime.json");
        if (!File.Exists(manifestPath))
            return new RuntimePackageStatus(id, false, null, 0, null, false);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var size = root.TryGetProperty("payloadSizeBytes", out var sizeElement) &&
                sizeElement.ValueKind == JsonValueKind.Number
                ? (long)sizeElement.GetDouble() : 0;
            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString() : null;
            var nodeIncluded = root.TryGetProperty("nodeIncluded", out var nodeElement) &&
                nodeElement.ValueKind == JsonValueKind.True;
            return new RuntimePackageStatus(id, true, directory, size, version, nodeIncluded);
        }
        catch (JsonException)
        {
            return new RuntimePackageStatus(id, false, null, 0, null, false);
        }
    }

    /// <summary>
    /// 从本地 zip 或 http(s) URL 安装运行时。source 为 zip 文件；
    /// expectedSha256 非空时必须匹配，避免把损坏/被替换的载荷装进用户目录。
    /// </summary>
    public async Task<RuntimePackageStatus> InstallAsync(
        string source,
        string? expectedSha256,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateId(id);
        if (expectedSha256 is not null)
        {
            if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("SHA-256 must be 64 hexadecimal characters.", nameof(expectedSha256));
        }

        Directory.CreateDirectory(_layout.RuntimeDirectory);
        var temporary = Path.Combine(_layout.RuntimeDirectory, $".{id}-{Guid.NewGuid():N}.download");
        var staging = Path.Combine(_layout.RuntimeDirectory, $".{id}-{Guid.NewGuid():N}.staging");
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await using var remote = await _http.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(temporary);
                await remote.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var local = Path.GetFullPath(source);
                if (!File.Exists(local)) throw new FileNotFoundException("Runtime package not found.", local);
                File.Copy(local, temporary, overwrite: true);
            }

            if (expectedSha256 is not null)
            {
                var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(temporary, cancellationToken)
                    .ConfigureAwait(false))).ToLowerInvariant();
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Runtime package SHA-256 mismatch. Expected {expectedSha256}, got {actual}.");
            }

            ExtractSafely(temporary, staging);
            if (!File.Exists(Path.Combine(staging, "runtime.json")))
                throw new InvalidDataException("Runtime package is missing runtime.json.");

            var target = Root(id);
            var backup = target + ".backup-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try
            {
                Directory.Move(staging, target);
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                if (Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }

            return GetStatus(id);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public void Remove(string id)
    {
        var target = Root(id);
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Runtime package contains an unsafe path: {entry.FullName}");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Invalid runtime id.", nameof(id));
    }
}
