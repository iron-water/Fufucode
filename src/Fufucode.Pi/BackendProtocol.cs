namespace Fufucode.Pi;

/// <summary>Version-specific wire differences; this is not RPC capability negotiation.</summary>
public enum BackendId { Pi, Omp }

public sealed record BackendCapabilities(BackendId Id, string Name, string VerifiedVersion,
    bool PersistentHistory, bool ApprovalBridge, bool ProjectFork)
{
    public static BackendCapabilities For(BackendId id) => id switch
    {
        BackendId.Pi => new(id, "Pi", "1.1.0", true, true, true),
        BackendId.Omp => new(id, "OMP", "18.8.5", true, true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static string StorageId(BackendId id) => For(id).Id == BackendId.Pi ? "pi" : "omp";
    public static BackendId Parse(string id) => id switch
    {
        "pi" => BackendId.Pi,
        "omp" => BackendId.Omp,
        _ => throw new InvalidOperationException($"Unknown session backend '{id}'; refusing to open with another engine.")
    };
}
