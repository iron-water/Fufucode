using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions.Automation.Harnesses;

namespace Fufucode.Core.Automation.Harnesses;

public abstract class BatchHarnessAdapter : IHarnessAdapter
{
    public abstract string Id { get; }
    public HarnessCapabilities Capabilities => new(true, true, false, false, false);
    public abstract ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession);
    public abstract HarnessSignal Parse(JsonElement record);

    protected ProcessStartInfo Start(HarnessRuntime runtime, string worktree)
    {
        if (runtime.Harness != Id) throw new ArgumentException("Protocol/runtime mismatch.");
        if (!Path.IsPathFullyQualified(runtime.Executable) || !File.Exists(runtime.Executable))
            throw new FileNotFoundException("Configure an absolute runtime executable.", runtime.Executable);
        // Resolve script shims to node + entrypoint, or explicitly configure pwsh + -File.
        if (Path.GetExtension(runtime.Executable).ToLowerInvariant() is ".cmd" or ".bat" or ".ps1")
            throw new NotSupportedException("Configure the native executable and argument array, not a shell shim.");
        var start = new ProcessStartInfo(runtime.Executable)
        {
            WorkingDirectory = worktree, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in runtime.PrefixArguments) start.ArgumentList.Add(argument);
        if (runtime.Environment is { } environment)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        return start;
    }

    protected static void Add(ProcessStartInfo start, params string[] args)
    { foreach (var arg in args) start.ArgumentList.Add(arg); }

    protected static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    protected static string AssistantText(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray().Where(b => Text(b, "type") == "text").Select(b => Text(b, "text"))) : "";

    protected static HarnessSignal PiRecord(JsonElement e)
    {
        var type = Text(e, "type");
        if (type == "session") return new("session", Text(e, "id"));
        if (type == "message_end" && e.TryGetProperty("message", out var message) && Text(message, "role") == "assistant")
        {
            var reason = Text(message, "stopReason");
            return new("message", Text: AssistantText(message), Success: reason is "error" or "aborted" ? false : null);
        }
        // agent_end closes the batch agent loop, but process exit is still required.
        if (type == "agent_end") return new("terminal", Success: true);
        return new("progress");
    }
}

public sealed class PiBatchAdapter : BatchHarnessAdapter
{
    public override string Id => "pi";
    public override ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession)
    {
        var s = Start(runtime, worktree);
        Add(s, "--print", "--mode", "json", "--model", request.Model,
            "--no-extensions", "--no-skills", "--no-mcp", "--session-dir", Path.Combine(runDirectory, "sessions"));
        if (request.Provider is not null) Add(s, "--provider", request.Provider);
        if (savedSession is not null)
        {
            if (!File.Exists(savedSession)) throw new FileNotFoundException("Saved Pi session is missing.", savedSession);
            Add(s, "--session", savedSession);
        }
        return s;
    }
    public override HarnessSignal Parse(JsonElement record) => PiRecord(record);
}

public sealed class OmpBatchAdapter : BatchHarnessAdapter
{
    public override string Id => "omp";
    public override ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession)
    {
        var s = Start(runtime, worktree);
        Add(s, "--print", "--mode", "json", "--model", request.Model, "--smol", request.Model,
            "--slow", request.Model, "--plan", request.Model, "--no-title", "--no-extensions",
            "--no-skills", "--no-rules", "--no-lsp", "--session-dir", Path.Combine(runDirectory, "sessions"));
        if (request.Provider is not null) Add(s, "--provider", request.Provider);
        if (savedSession is not null)
        {
            if (!File.Exists(savedSession)) throw new FileNotFoundException("Saved OMP session is missing.", savedSession);
            Add(s, "--resume", savedSession);
        }
        return s;
    }
    public override HarnessSignal Parse(JsonElement record) => PiRecord(record);
}

public sealed class CodexBatchAdapter : BatchHarnessAdapter
{
    public override string Id => "codex";
    public override ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession)
    {
        var s = Start(runtime, worktree);
        var sandbox = runtime.CodexSandbox ?? "workspace-write";
        if (sandbox is not ("read-only" or "workspace-write" or "danger-full-access"))
            throw new ArgumentException("Configure a supported explicit Codex deployment sandbox.");
        Add(s, "-a", "never", "exec", "--sandbox", sandbox);
        if (savedSession is not null) Add(s, "resume");
        Add(s, "--model", request.Model, "--json", "--skip-git-repo-check");
        if (savedSession is not null) Add(s, savedSession);
        Add(s, "-");
        return s;
    }
    public override HarnessSignal Parse(JsonElement e) => Text(e, "type") switch
    {
        "thread.started" => new("session", Text(e, "thread_id")),
        "turn.completed" => new("terminal", Success: true),
        "turn.failed" => new("terminal", Success: false),
        "item.completed" when e.TryGetProperty("item", out var item) && Text(item, "type") == "agent_message"
            => new("message", Text: Text(item, "text")),
        _ => new("progress")
    };
}

public sealed class DshBatchAdapter : BatchHarnessAdapter
{
    public override string Id => "dsh";
    public override ProcessStartInfo Prepare(HarnessRuntime runtime, HarnessRequest request,
        string worktree, string runDirectory, string? savedSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Provider);
        var s = Start(runtime, worktree);
        var overlay = Path.Combine(runDirectory, "model.patch.json");
        // JSON is a YAML subset accepted by the installed Cordis patch loader.
        File.WriteAllText(overlay, JsonSerializer.Serialize(new object[] {
            new { id = "agent-default-model", config = new { provider = request.Provider, model = request.Model } },
            new { id = "session-title-llm", disabled = true }
        }), new UTF8Encoding(false));
        Add(s, "--profile", "headless", "--patch", overlay, "--json");
        if (savedSession is not null) Add(s, "--session-id", savedSession);
        Add(s, "-");
        return s;
    }
    public override HarnessSignal Parse(JsonElement e)
    {
        var type = Text(e, "type");
        if (type == "session") return new("session", Text(e, "sessionId"));
        if (type == "final") return new("message", Text: Text(e, "text"));
        if (type == "error") return new("terminal", Success: false);
        if (type == "status" && Text(e, "phase") == "turn_end")
            return new("terminal", Success: e.TryGetProperty("reason", out var r) && Text(r, "kind") == "completed");
        return new("progress");
    }
}
