
namespace Fufucode.Core;

/// <summary>Value is null unless every included assistant reports this field. Subtotal only covers known values.</summary>
public sealed record UsageCounter(long? Value, long? KnownSubtotal, int KnownMessageCount, int UnknownMessageCount);

public sealed record UsageTotals(int AssistantMessageCount, UsageCounter Input, UsageCounter Output,
    UsageCounter CacheRead, UsageCounter CacheWrite, UsageCounter TotalTokens);

/// <summary>Turn zero contains assistant messages before the first user; other turns start at a user message.</summary>
public sealed record TurnUsage(int TurnNumber, int? UserMessageIndex, UsageTotals Usage,
    int ExcludedActiveAssistantCount)
{
    /// <summary>Present for persisted entry-tree queries; null for a plain message list.</summary>
    public string? UserEntryId { get; init; }
}

public sealed record SessionUsageReport(UsageTotals Session, IReadOnlyList<TurnUsage> Turns,
    int ExcludedActiveAssistantCount);

/// <summary>Pure projection of the current message list, not an accumulator of streaming events.</summary>
public static class SessionUsageQuery
{
    /// <summary>
    /// Counts each persisted assistant entry once across all branches. Turns follow the nearest user
    /// ancestor, not append order; current-branch-only totals use Query(history.CurrentBranchMessages).
    /// Compacted and abandoned messages still represent recorded usage and remain included.
    /// </summary>
    public static SessionUsageReport QueryHistory(SessionHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var entries = history.Entries;
        var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var users = new List<(HistoryEntry Entry, int MessageIndex)>();
        var groups = new Dictionary<string, List<PiMessage>>(StringComparer.Ordinal);
        var assistants = new List<PiMessage>();
        var unanchored = new List<PiMessage>();
        var messageIndex = 0;
        foreach (var entry in entries)
        {
            if (entry.Message is null) continue;
            if (entry.Message.Role == "user")
            {
                users.Add((entry, messageIndex));
                groups.Add(entry.Id, []);
            }
            messageIndex++;
        }
        foreach (var entry in entries)
        {
            if (entry.Message is not { Role: "assistant" } message) continue;
            assistants.Add(message);
            var parentId = entry.ParentId;
            var visited = new HashSet<string>(StringComparer.Ordinal) { entry.Id };
            string? userId = null;
            while (parentId is not null)
            {
                if (!visited.Add(parentId)) throw new RpcException("Usage ancestry contains a cycle.");
                if (!byId.TryGetValue(parentId, out var parent))
                    throw new RpcException("Usage ancestry references an unknown entry.");
                if (parent.Message?.Role == "user") { userId = parent.Id; break; }
                parentId = parent.ParentId;
            }
            if (userId is null) unanchored.Add(message);
            else groups[userId].Add(message);
        }
        var turns = new List<TurnUsage>();
        if (unanchored.Count != 0) turns.Add(new TurnUsage(0, null, Sum(unanchored), 0));
        for (var index = 0; index < users.Count; index++)
        {
            var (entry, userMessageIndex) = users[index];
            turns.Add(new TurnUsage(index + 1, userMessageIndex, Sum(groups[entry.Id]), 0)
                { UserEntryId = entry.Id });
        }
        return new SessionUsageReport(Sum(assistants), turns.ToArray(), 0);
    }

    /// <summary>
    /// Includes final assistant messages only when an active index is supplied. TotalTokens comes from
    /// the provider's reported field and is never inferred from other fields. The query does not deduplicate
    /// messages: distinct assistant messages in a tool loop each represent a separate model response.
    /// </summary>
    public static SessionUsageReport Query(IEnumerable<PiMessage> messages, int? activeMessageIndex = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var source = messages.ToArray();
        if (activeMessageIndex is { } active && (active < 0 || active >= source.Length))
            throw new ArgumentOutOfRangeException(nameof(activeMessageIndex));
        var completed = new List<PiMessage>();
        var turnMessages = new List<PiMessage>();
        var turns = new List<TurnUsage>();
        int? userIndex = null;
        var turnNumber = 0;
        var excluded = 0;
        var turnExcluded = 0;
        void FinishTurn()
        {
            if (userIndex is not null || turnMessages.Count != 0 || turnExcluded != 0)
                turns.Add(new TurnUsage(turnNumber, userIndex, Sum(turnMessages), turnExcluded));
            turnMessages.Clear();
            turnExcluded = 0;
        }
        for (var index = 0; index < source.Length; index++)
        {
            var message = source[index];
            if (message.Role == "user")
            {
                FinishTurn();
                userIndex = index;
                turnNumber++;
            }
            else if (message.Role == "assistant")
            {
                if (index == activeMessageIndex)
                {
                    excluded++;
                    turnExcluded++;
                }
                else
                {
                    completed.Add(message);
                    turnMessages.Add(message);
                }
            }
        }
        FinishTurn();
        return new SessionUsageReport(Sum(completed), turns.ToArray(), excluded);
    }

    private static UsageTotals Sum(IReadOnlyList<PiMessage> messages) => new(messages.Count,
        Count(messages, usage => usage.Input), Count(messages, usage => usage.Output),
        Count(messages, usage => usage.CacheRead), Count(messages, usage => usage.CacheWrite),
        Count(messages, usage => usage.TotalTokens));

    private static UsageCounter Count(IReadOnlyList<PiMessage> messages, Func<PiUsage, long?> field)
    {
        long subtotal = 0;
        var known = 0;
        foreach (var message in messages)
            if (message.Usage is { } usage && field(usage) is { } value)
            {
                subtotal = checked(subtotal + value);
                known++;
            }
        var unknown = messages.Count - known;
        return new UsageCounter(known > 0 && unknown == 0 ? subtotal : null,
            known > 0 ? subtotal : null, known, unknown);
    }
}
