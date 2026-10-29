using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Fufucode.Core.Security;

public sealed record SecurityBackupManifest(string OriginalPath, bool Existed, string? ContentFile, DateTimeOffset At);

/// <summary>Backups protect native write_file/edit_file only. Shell/plugin writes cannot be intercepted.</summary>
public sealed class SecurityBackupStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    public string DirectoryPath { get; }
    public SecurityBackupStore(string dataRoot)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(dataRoot), "security-backups");
        _gate = Gates.GetOrAdd(DirectoryPath, _ => new object());
    }
    public string Capture(string path, SecuritySettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            EnsureNotLink(DirectoryPath);
            var target = SecurityPolicyEvaluator.RealPath(path);
            var existed = File.Exists(target);
            var length = existed ? new FileInfo(target).Length : 0;
            if (length > settings.BackupFileMb * 1024L * 1024) throw new IOException("安全备份失败：文件超过单文件限额，已停止写入。");
            var used = Directory.EnumerateFiles(DirectoryPath).Sum(f => new FileInfo(f).Length);
            var id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            var content = existed ? id + ".bin" : null;
            var manifest = new SecurityBackupManifest(target, existed, content, DateTimeOffset.UtcNow);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, SecurityPolicyStore.Json);
            if (used + length + json.Length + 96 > settings.BackupTotalMb * 1024L * 1024)
                throw new IOException("安全备份失败：总配额不足，请清理备份后重试；已停止写入。");
            var manifestPath = Path.Combine(DirectoryPath, id + ".json");
            try
            {
                if (existed)
                {
                    using var source = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(Path.Combine(DirectoryPath, content!), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(output);
                    if (output.Length != length) throw new IOException("安全备份期间文件发生变化。");
                    output.Flush(true);
                }
                using var metadata = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                metadata.Write(json);
                metadata.Flush(true);
                var signature = Sign(json, content is null ? null : Path.Combine(DirectoryPath, content));
                File.WriteAllText(manifestPath + ".sig", signature);
                return manifestPath;
            }
            catch
            {
                if (content is not null && File.Exists(Path.Combine(DirectoryPath, content))) File.Delete(Path.Combine(DirectoryPath, content));
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                throw;
            }
        }
    }
    private void EnsureNotLink(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), SecurityPolicyEvaluator.RealPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup path contains a symbolic link.");
    }
    private string Sign(byte[] manifest, string? contentPath)
    {
        var keyPath = Path.Combine(DirectoryPath, ".integrity-key");
        EnsureNotLink(keyPath);
        if (!File.Exists(keyPath))
        {
            using var keyFile = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            keyFile.Write(RandomNumberGenerator.GetBytes(32));
            keyFile.Flush(true);
        }
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, File.ReadAllBytes(keyPath));
        hash.AppendData(manifest);
        if (contentPath is not null)
        {
            using var input = File.OpenRead(contentPath);
            var buffer = new byte[81920];
            int n;
            while ((n = input.Read(buffer)) > 0) hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    /// <summary>Explicit user recovery. Review OriginalPath first; this can overwrite the current file.</summary>
    public void Restore(string manifestPath)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(manifestPath);
            if (!string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Manifest must be in the backup directory.");
            EnsureNotLink(full);
            var raw = File.ReadAllBytes(full);
            var manifest = JsonSerializer.Deserialize<SecurityBackupManifest>(raw, SecurityPolicyStore.Json)
                ?? throw new InvalidDataException("Invalid backup manifest.");
            var contentPath = manifest.ContentFile is null ? null : Path.Combine(DirectoryPath, manifest.ContentFile);
            if (manifest.Existed && (manifest.ContentFile is null || Path.GetFileName(manifest.ContentFile) != manifest.ContentFile))
                throw new InvalidDataException("Invalid content path.");
            if (contentPath is not null) EnsureNotLink(contentPath);
            EnsureNotLink(full + ".sig");
            var expected = Encoding.ASCII.GetBytes(Sign(raw, contentPath));
            var actual = Encoding.ASCII.GetBytes(File.ReadAllText(full + ".sig"));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new InvalidDataException("备份或原路径清单被修改，拒绝恢复。");
            var target = Path.GetFullPath(manifest.OriginalPath);
            if (!string.Equals(target, SecurityPolicyEvaluator.RealPath(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原路径现在包含符号链接，拒绝恢复。");
            var policy = new SecurityPolicyStore(Path.GetDirectoryName(DirectoryPath)!).Current;
            var evaluator = new SecurityPolicyEvaluator(new SecurityPolicyStore(Path.GetDirectoryName(DirectoryPath)!), Path.GetDirectoryName(target)!);
            if (policy.Enabled && evaluator.FileAction(target, manifest.Existed ? "write" : "delete") == SecurityAction.Deny)
                throw new UnauthorizedAccessException("当前安全规则禁止恢复此路径。");
            // Preserve the file being replaced/deleted, so recovery itself remains recoverable.
            Capture(target, policy);
            if (!manifest.Existed)
            {
                if (File.Exists(target)) File.Delete(target);
                new SecurityAuditStore(Path.GetDirectoryName(DirectoryPath)!).Record("restore", SecurityAction.Allow,
                    "用户恢复新增文件前的状态；删除前已创建恢复点。", policy);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".restore-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(contentPath!, temp);
                File.Move(temp, target, true);
                new SecurityAuditStore(Path.GetDirectoryName(DirectoryPath)!).Record("restore", SecurityAction.Allow,
                    "用户恢复已校验的安全备份；覆盖前已创建新的恢复点。", policy);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
