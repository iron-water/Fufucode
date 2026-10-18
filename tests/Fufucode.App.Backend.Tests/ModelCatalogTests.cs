using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// OpenAI 兼容端点的模型目录测试（对接本机 Magpie 等网关）：
/// 1. 解析 data[] 形状（Magpie / OpenAI）；
/// 2. 解析 models[] 形状（Ollama 等）；
/// 3. 读取 display_name、context_length、推理档位；
/// 4. 去重、跳过无 id 的条目、按 id 排序；
/// 5. URL 归一化：/v1、/v1/chat/completions、/v1/models 都能拼出 /v1/models；
/// 6. 真实 HTTP 往返与失败路径。
/// </summary>
internal static class ModelCatalogTests
{
    public static async Task RunAsync(Checks checks)
    {
        // 1. Magpie / OpenAI：data[]
        const string openAiShape = """
        {
          "object": "list",
          "data": [
            {
              "id": "group/auto-glm-5-3-flash",
              "display_name": "GLM-5.3-Flash",
              "context_length": 1048576,
              "supported_reasoning_levels": [ { "effort": "low" }, { "effort": "high" } ]
            },
            { "id": "group/auto-gpt-6-1-sol", "display_name": "GPT-6.1 Sol", "context_window": 500000 }
          ]
        }
        """;
        var openAi = ModelCatalogClient.Parse(System.Text.Json.JsonDocument.Parse(openAiShape).RootElement);
        checks.Equal(2, openAi.Count, "解析 data[] 形状");
        checks.Equal("group/auto-glm-5-3-flash", openAi[0].Id, "模型 id 保留 group/ 前缀");
        checks.Equal("GLM-5.3-Flash", openAi[0].DisplayName, "读取 display_name");
        checks.Equal((long?)1048576, openAi[0].ContextLength, "读取 context_length");
        checks.Equal(2, openAi[0].ReasoningEfforts.Count, "读取推理档位");
        checks.Equal((long?)500000, openAi[1].ContextLength, "兼容 context_window 字段");

        // 2. Ollama：models[]，无 display_name
        const string ollamaShape = """
        { "models": [ { "name": "qwen3:8b", "size": 5000000000 }, { "name": "llama3:8b" } ] }
        """;
        var ollama = ModelCatalogClient.Parse(System.Text.Json.JsonDocument.Parse(ollamaShape).RootElement);
        checks.Equal(2, ollama.Count, "解析 models[] 形状");
        checks.Check(ollama.All(m => m.DisplayName == m.Id), "缺少 display_name 时回退到 id");

        // 3. 去重、跳过无 id、排序
        const string messy = """
        {
          "data": [
            { "id": "b-model" },
            { "id": "a-model", "display_name": "A" },
            { "id": "a-model", "display_name": "A duplicate" },
            { "display_name": "no id" },
            { "id": "" }
          ]
        }
        """;
        var cleaned = ModelCatalogClient.Parse(System.Text.Json.JsonDocument.Parse(messy).RootElement);
        checks.Equal(2, cleaned.Count, "去重并跳过无 id 的条目");
        checks.Equal("a-model", cleaned[0].Id, "按 id 排序");
        checks.Equal("b-model", cleaned[1].Id, "第二项按 id 排序");

        // 4. 空与异常形状不崩溃
        checks.Equal(0, ModelCatalogClient.Parse(System.Text.Json.JsonDocument.Parse("{}").RootElement).Count,
            "缺少 data/models 时返回空列表");
        checks.Equal(0, ModelCatalogClient.Parse(System.Text.Json.JsonDocument.Parse("[]").RootElement).Count,
            "空数组返回空列表");

        // 5. Label 展示
        checks.Equal("group/auto-glm-5-3-flash", new ModelEntry(
            "group/auto-glm-5-3-flash", "group/auto-glm-5-3-flash", null, []).Label,
            "名称与 id 相同时只显示 id");
        checks.Check(new ModelEntry("id", "Name", null, []).Label.Contains("Name"), "名称不同时带名称");

        // 6. 真实 HTTP：200 正常解析、非 200 抛出、URL 归一化
        using (var handler = new StubHandler((request, _) =>
        {
            checks.Check(request.RequestUri!.AbsoluteUri.EndsWith("/v1/models", StringComparison.Ordinal),
                "从 /v1 拼出 /v1/models");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openAiShape, Encoding.UTF8, "application/json"),
            };
        }))
        using (var client = new ModelCatalogClient(new HttpClient(handler)))
        {
            var models = await client.ListAsync("http://127.0.0.1:3425/v1");
            checks.Equal(2, models.Count, "HTTP 往返解析模型目录");
        }

        using (var handler = new StubHandler((request, _) =>
        {
            checks.Check(request.RequestUri!.AbsoluteUri.EndsWith("/v1/models", StringComparison.Ordinal),
                "从 /v1/chat/completions 归一化到 /v1/models");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }))
        using (var client = new ModelCatalogClient(new HttpClient(handler)))
        {
            checks.ThrowsAsync<HttpRequestException>(
                () => client.ListAsync("http://127.0.0.1:3425/v1/chat/completions"),
                "非 2xx 如实抛出而不是返回空目录").GetAwaiter().GetResult();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request, cancellationToken));
    }
}
