using System.Text.Json;
using Fufucode.Core;
using Fufucode.Core.Backend;

/// <summary>代码审核修复的定向回归：会话文件恢复、流式工具分片与撤销备份数据根。</summary>
internal static class ReviewRegressionTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static void Run(string root)
    {
        LoadNativeHistory();
        StreamingToolCallWithoutIndex();
        JournalFollowsConfiguredRoot(root);
        ModelClientDisposesOwnedHttpClient();
    }

    private static void ModelClientDisposesOwnedHttpClient()
    {
        var client = new OpenAiCompatibleModelClient("http://127.0.0.1:1/v1");
        var http = (HttpClient?)typeof(OpenAiCompatibleModelClient)
            .GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(client);
        client.Dispose();
        var disposed = false;
        try
        {
            if (http is null) throw new InvalidOperationException("HttpClient field missing");
            http.GetAsync("http://127.0.0.1:1/").GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException) { disposed = true; }
        catch (HttpRequestException) { }
        Check(disposed, "模型客户端释放其自建的 HttpClient，避免会话开关累积连接池");
    }

    private static void LoadNativeHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fufucode-native-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var corrupt = Path.Combine(directory, "bad.json");
            File.WriteAllText(corrupt, "{ this is not json");
            var parsed = PersistentSessionManager.LoadNativeMessages(corrupt);
            Check(parsed is null, "损坏的 FUMO 历史不会作为有效历史返回");
            Check(!File.Exists(corrupt), "确实无法解析的文件被隔离移走");
            Check(Directory.GetFiles(directory, "bad.json.corrupt-*").Length == 1,
                "隔离文件保留唯一副本，不覆盖历史损坏副本");

            var valid = Path.Combine(directory, "good.json");
            File.WriteAllText(valid, """{"version":2,"messages":[{"Role":0,"Content":"hello"}]}""");
            Check(PersistentSessionManager.LoadNativeMessages(valid) is { Messages.Count: 1 },
                "有效历史仍可正常读取，文件保持原位");
            Check(File.Exists(valid), "有效历史读取后原文件仍存在");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static void StreamingToolCallWithoutIndex()
    {
        var parser = new ChatCompletionSseParser();
        void Feed(object payload) => parser.TryApply(
            new ServerSentEvent(null, JsonSerializer.Serialize(payload), null, null), null);

        Feed(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new { id = "call_1", function = new { name = "read_file", arguments = "{\"path\":" } },
                        },
                    },
                },
            },
        });
        // 兼容网关常见：后续参数分片省略 index。
        Feed(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { function = new { arguments = "\"a\"}" } } } } } } });

        var calls = parser.Completion.Primary?.ToolCalls;
        Check(calls is { Count: 1 },
            "缺少 index 的后续工具分片仍拼接到同一调用，而不是拆成第二个调用");
        Check(calls![0].ArgumentsJson == "{\"path\":\"a\"}",
            "省略 index 的参数分片按顺序完整拼接");
    }

    private static void JournalFollowsConfiguredRoot(string root)
    {
        var previous = FileChangeJournal.Root;
        var dataRoot = Path.Combine(root, "tmp", "journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            FileChangeJournal.Configure(dataRoot);
            var target = Path.Combine(dataRoot, "note.txt");
            File.WriteAllText(target, "v1");
            FileChangeJournal.Capture("review-fix-" + Guid.NewGuid().ToString("N"), target);
            var backups = Path.Combine(dataRoot, "turn-backups");
            Check(Directory.Exists(backups) && Directory.GetFiles(backups).Length > 0,
                "撤销备份写入显式配置的数据根，而不是用户默认数据根");
        }
        finally
        {
            var parent = Path.GetDirectoryName(previous);
            if (parent is not null) FileChangeJournal.Configure(parent);
        }
    }
}
