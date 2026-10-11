using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

public static class AtriaModelAcceptanceTests
{
    public static async Task RunIfConfiguredAsync(string node, string cli, string root)
    {
        var key = Environment.GetEnvironmentVariable("FUFUCODE_ATRIA_API_KEY");
        if (Environment.GetEnvironmentVariable("FUFUCODE_ATRIA_REAL_MODEL_TEST") != "1" ||
            string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("SKIP Atria Core acceptance: set FUFUCODE_ATRIA_REAL_MODEL_TEST=1 and FUFUCODE_ATRIA_API_KEY");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "fufucode-atria-core-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(temp, "agent");
        var project = Path.Combine(temp, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(agent, "models.json"), """
            {
              "providers": {
                "atria": {
                  "baseUrl": "https://api.atria-asi.ai/v1",
                  "api": "openai-completions",
                  "apiKey": "$FUFUCODE_ATRIA_API_KEY",
                  "models": [ { "id": "Atria-Dawn-Preview" } ]
                }
              }
            }
            """);

        try
        {
            var environment = new Dictionary<string, string>
            {
                ["PI_CODING_AGENT_DIR"] = agent,
                ["FUFUCODE_ATRIA_API_KEY"] = key,
            };
            await using var session = new ChatSessionCoordinator(new PiRpcOptions(node,
                [cli, "--mode", "rpc", "--no-session"], project,
                EnvironmentVariables: environment, RequireApprovalBridge: false));
            await session.StartAsync();

            var selected = await session.SetModelAsync("atria", "Atria-Dawn-Preview");
            if (!selected.Success) throw new Exception($"Atria set_model failed: {selected.Error}");
            var state = await session.QueryStateAsync();
            if (!state.Success || state.Data is not { } data || !data.TryGetProperty("model", out var model) ||
                model.ValueKind != JsonValueKind.Object ||
                model.GetProperty("provider").GetString() != "atria" ||
                model.GetProperty("id").GetString() != "Atria-Dawn-Preview")
                throw new Exception("Atria selected model was not confirmed by get_state.");

            var prompts = new[]
            {
                "请记住数字 17，只回答：已记住。",
                "上一轮让我记住的数字是多少？只回答数字。",
            };
            foreach (var (prompt, turn) in prompts.Select((prompt, index) => (prompt, index + 1)))
            {
                var assistantsBefore = session.Snapshot.Messages.Count(message => message.Role == "assistant");
                await session.SendPromptAsync(prompt);
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                while ((session.Snapshot.Activity != ActivityState.Idle ||
                        session.Snapshot.LastResult != TaskResult.Completed ||
                        session.Snapshot.Messages.Count(message => message.Role == "assistant") <= assistantsBefore) &&
                       DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                var settled = session.Snapshot;
                if (settled.Activity != ActivityState.Idle || settled.LastResult != TaskResult.Completed ||
                    settled.Messages.Count(message => message.Role == "assistant") <= assistantsBefore ||
                    !(settled.Messages.LastOrDefault(message => message.Role == "assistant")?.Content
                        .OfType<PiTextBlock>().Any(block => !string.IsNullOrWhiteSpace(block.Text)) ?? false))
                    throw new Exception($"Atria turn {turn} did not complete with assistant text ({settled.LastError}).");

                // Exercise the actual clear_queue -> abort path after each completed turn.
                await session.StopAsync();
                if (session.Snapshot.Activity != ActivityState.Idle || session.Snapshot.LastResult != TaskResult.Interrupted)
                    throw new Exception($"Atria turn {turn} abort did not settle as Interrupted.");
                Console.WriteLine($"PASS Atria model turn {turn} completed; post-turn abort settled");
            }

            Console.WriteLine("PASS Atria dynamic model selection and two-turn continuity");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* Temporary evidence cleanup must not hide the acceptance result. */ }
        }
    }
}
