using System.Text.RegularExpressions;

namespace Fufucode.Core.Security;

public enum SecurityAction { Allow, Ask, Deny }
public sealed record FileSecurityRule(string Pattern, SecurityAction Read = SecurityAction.Ask,
    SecurityAction Write = SecurityAction.Ask, SecurityAction Delete = SecurityAction.Ask);
public sealed record SecurityRule(string Pattern, SecurityAction Action = SecurityAction.Ask);
public sealed record SecretDetectionRule(string Name, string Pattern, bool Enabled = true);

/// <summary>Local native-tool guards. This policy does not provide OS process isolation.</summary>
public sealed record SecuritySettings
{
    public bool Enabled { get; init; } = true;
    public bool BehaviorReviewEnabled { get; init; } = true;
    public bool InjectionProtectionEnabled { get; init; } = true;
    public bool SensitiveDataProtectionEnabled { get; init; } = true;
    public bool BackupEnabled { get; init; } = true;
    public int BackupTotalMb { get; init; } = 3000;
    public int BackupFileMb { get; init; } = 100;
    public bool AuditEnabled { get; init; } = true;
    public SecurityAction RemoteHttpAction { get; init; } = SecurityAction.Ask;
    public SecurityAction LocalHttpAction { get; init; } = SecurityAction.Allow;
    public IReadOnlyList<FileSecurityRule> FileRules { get; init; } =
    [
        new("**/.ssh/**"), new("**/.aws/**"), new("**/.azure/**"), new("**/.gnupg/**"),
        new("**/.kube/**"), new("**/.env*"), new("**/secrets/**"), new("**/*credentials*"),
        new("**/AppData/**"), new("**/.config/**"), new("**/.fumo/**")
    ];
    public IReadOnlyList<SecurityRule> CommandRules { get; init; } = [];
    public IReadOnlyList<SecurityRule> NetworkRules { get; init; } = [];
    public IReadOnlyList<string> BlockedPrograms { get; init; } = [];
    public IReadOnlyList<string> ReviewInstructions { get; init; } = [];
    public IReadOnlyList<SecretDetectionRule> SecretRules { get; init; } =
    [
        new("Private key", @"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)"),
        new("AWS access key", @"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
        new("GitHub token", @"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b"),
        new("API token", @"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{16,})\b"),
        new("Assigned credential", "(?i)(?:api[_-]?key|secret|pass(?:word)|access[_-]?token)\\s*[=:]\\s*[\"']?[^\\s\"',;}{]{8,}")
    ];

    internal SecuritySettings Validate()
    {
        if (!Enum.IsDefined(RemoteHttpAction) || !Enum.IsDefined(LocalHttpAction) ||
            BackupTotalMb is < 1 or > 1_000_000 || BackupFileMb is < 1 or > 1_000_000 ||
            BackupFileMb > BackupTotalMb)
            throw new ArgumentException("Invalid security action or backup quota.");
        if (FileRules is null || CommandRules is null || NetworkRules is null || BlockedPrograms is null ||
            ReviewInstructions is null || SecretRules is null) throw new ArgumentException("Security lists cannot be null.");
        if (ReviewInstructions.Count > 5 || ReviewInstructions.Any(x => x is null || x.Length > 300))
            throw new ArgumentException("Review instructions: at most five entries of 300 characters.");
        foreach (var r in FileRules)
            if (r is null || !ValidPattern(r.Pattern) || !Enum.IsDefined(r.Read) || !Enum.IsDefined(r.Write) || !Enum.IsDefined(r.Delete))
                throw new ArgumentException("Invalid file rule.");
        foreach (var r in CommandRules.Concat(NetworkRules))
            if (r is null || !ValidPattern(r.Pattern) || !Enum.IsDefined(r.Action)) throw new ArgumentException("Invalid rule.");
        if (FileRules.Count + CommandRules.Count + NetworkRules.Count + SecretRules.Count > 1000 ||
            BlockedPrograms.Any(x => !ValidPattern(x))) throw new ArgumentException("Invalid security list.");
        foreach (var r in SecretRules)
        {
            if (r is null || !ValidPattern(r.Name) || !ValidPattern(r.Pattern)) throw new ArgumentException("Invalid secret rule.");
            _ = new Regex(r.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        return this with
        {
            FileRules = Array.AsReadOnly(FileRules.ToArray()), CommandRules = Array.AsReadOnly(CommandRules.ToArray()),
            NetworkRules = Array.AsReadOnly(NetworkRules.ToArray()), BlockedPrograms = Array.AsReadOnly(BlockedPrograms.ToArray()),
            ReviewInstructions = Array.AsReadOnly(ReviewInstructions.ToArray()), SecretRules = Array.AsReadOnly(SecretRules.ToArray())
        };
    }
    private static bool ValidPattern(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096;
}
