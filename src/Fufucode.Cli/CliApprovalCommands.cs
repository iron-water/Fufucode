using System.Text;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

namespace Fufucode.Cli;

public static class CliApprovalCommands
{
    public static async Task ExecuteAsync(IExtensionUiSession session, IReadOnlyList<string> args,
        bool json, TextWriter output, CancellationToken cancellationToken = default,
        Action<ExtensionUiRequest, PiExtensionUiResponse>? beforeRespond = null)
    {
        if (args.SequenceEqual(["list"]))
        {
            var requests = session.PendingExtensionUiRequests;
            if (json) await output.WriteLineAsync(JsonSerializer.Serialize(requests.Select(r => new
            { id = r.Id, method = r.Method, payload = r.Payload, receivedAt = r.ReceivedAt, timeoutMs = r.Timeout.TotalMilliseconds })));
            else foreach (var request in requests)
                await output.WriteLineAsync($"{request.Id}  {request.Method}  {request.Payload.GetRawText()}");
            await output.FlushAsync(cancellationToken);
            return;
        }
        if (args.Count < 2 || args[0] is not ("accept" or "reject") ||
            (args[0] == "reject" && args.Count != 2) || args.Count > 3)
            throw new ArgumentException("Usage: approve list | accept <id> [value] | reject <id>.");
        // Preserve the exact Core-issued token including generation, method and payload.
        var token = session.PendingExtensionUiRequests.FirstOrDefault(r => r.Id == args[1]) ??
            throw new InvalidOperationException("Approval request is absent or expired.");
        var accept = args[0] == "accept";
        var response = !accept ? token.Method == "confirm" ? PiExtensionUiResponse.Confirm(false) : PiExtensionUiResponse.Cancel()
            : Accept(token, args.Count == 3 ? args[2] : null);
        beforeRespond?.Invoke(token, response);
        await session.RespondToExtensionUiAsync(token, response, cancellationToken);
        await output.WriteLineAsync(json ? JsonSerializer.Serialize(new { id = token.Id, accepted = accept })
            : $"{token.Id}: {(accept ? "accepted" : "rejected")}");
        await output.FlushAsync(cancellationToken);
    }

    internal static PiExtensionUiResponse Accept(ExtensionUiRequest token, string? value)
    {
        switch (token.Method)
        {
            case "confirm":
                if (value is not null) throw new ArgumentException("confirm does not take a value.");
                return PiExtensionUiResponse.Confirm(true);
            case "select":
                if (value is null || !Options(token).Contains(value, StringComparer.Ordinal))
                    throw new ArgumentException("select requires an exact option value.");
                return PiExtensionUiResponse.WithValue(value);
            case "input" or "editor":
                if (value is null) throw new ArgumentException("input/editor requires a value (use \"\" for empty).");
                return PiExtensionUiResponse.WithValue(value);
            default: throw new ArgumentException("Unsupported approval method.");
        }
    }

    internal static string[] Options(ExtensionUiRequest token) => token.Payload.ValueKind == JsonValueKind.Object &&
        token.Payload.TryGetProperty("options", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
}

/// <summary>REPL tokenization: quotes group values; backslashes remain literal for Windows paths.</summary>
public static class CliCommandLine
{
    public static string[] Split(string line)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0'; var started = false;
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else word.Append(c);
            }
            else if (c is '\'' or '"') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started) { result.Add(word.ToString()); word.Clear(); started = false; }
            }
            else { word.Append(c); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("Unclosed quote.");
        if (started) result.Add(word.ToString());
        return result.ToArray();
    }
}
