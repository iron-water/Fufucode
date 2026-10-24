using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Fufucode.App.Features.ExternalBrowser;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.Browser.Tests;

/// <summary>
/// 外部浏览器桥的真实 HTTP 协议测试：状态、PIN 配对、长轮询下发、结果回传、页面引用、
/// 鉴权与站点权限拒绝。用真实回环端口，不接触真实 Chrome。
/// </summary>
internal static class ExternalBrowserBridgeTests
{
    public static void Run(Action<bool, string> check)
    {
        var settings = BrowserSettings.Default;
        var server = new ExternalBrowserBridgeServer(() => settings, "test-token", "1234");
        ExternalBrowserPageReference? collected = null;
        server.PageReferenceHandler = reference => { collected = reference; return Task.FromResult("已插入草稿"); };
        try
        {
            check(server.StartAsync(0).GetAwaiter().GetResult(), "外部浏览器桥可在回环端口启动");
            check(server.Running && server.Port > 0, "外部浏览器桥报告真实监听端口");

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
            var status = GetJson(http, "/api/status", null);
            check(status.TryGetProperty("paired", out var paired) && paired.GetBoolean() is false,
                "未配对时 /api/status 报告 paired=false");

            var wrong = Send(http, HttpMethod.Post, "/api/pair", "{\"pin\":\"0000\"}", null);
            check(wrong.Status == HttpStatusCode.Unauthorized, "错误 PIN 被拒绝");

            var pair = Send(http, HttpMethod.Post, "/api/pair", "{\"pin\":\"1234\"}", null);
            using var pairDoc = JsonDocument.Parse(pair.Body);
            check(pair.Status == HttpStatusCode.OK &&
                  pairDoc.RootElement.GetProperty("token").GetString() == "test-token",
                "正确 PIN 返回真实长期令牌");

            var handshake = Send(http, HttpMethod.Post, "/api/extension/handshake",
                "{\"clientId\":\"test-client\",\"browser\":\"Chrome\",\"browserVersion\":\"test\",\"tabCount\":2,\"activeUrl\":\"https://example.com\",\"capabilities\":[\"state\",\"tabs\",\"click\"]}",
                "test-token");
            check(handshake.Status == HttpStatusCode.OK, "扩展配对后完成握手");
            var ready = GetJson(http, "/api/status", "test-token");
            check(ready.GetProperty("handshake").GetBoolean() &&
                  ready.GetProperty("clientId").GetString() == "test-client" &&
                  ready.GetProperty("tabCount").GetInt32() == 2,
                "状态接口报告真实扩展身份、握手与标签页快照");

            var unauthorized = Send(http, HttpMethod.Get, "/api/next", null, null);
            check(unauthorized.Status == HttpStatusCode.Unauthorized, "未授权请求不能取命令");

            var execution = server.ExecuteAsync("state", "{}");
            var command = GetJson(http, "/api/next", "test-token");
            check(command.GetProperty("action").GetString() == "state", "长轮询下发真实 action");
            var id = command.GetProperty("id").GetString();
            var result = Send(http, HttpMethod.Post, "/api/result",
                "{\"id\":" + JsonSerializer.Serialize(id) + ",\"ok\":true,\"result\":{\"ok\":true,\"url\":\"https://example.com/x\",\"title\":\"Example\"}}",
                "test-token");
            check(result.Status == HttpStatusCode.OK, "扩展结果被桥接收");
            var payload = execution.GetAwaiter().GetResult();
            check(payload.Contains("example.com", StringComparison.Ordinal), "动作结果原样回传给调用方");
            check(server.LastPageHost == "example.com", "桥从结果中更新当前页面主机");

            var collect = Send(http, HttpMethod.Post, "/api/extension/collect",
                "{\"title\":\"T\",\"url\":\"https://example.com/a\",\"content\":\"正文\",\"selector\":\"#main\"}",
                "test-token");
            check(collect.Status == HttpStatusCode.OK && collected is not null &&
                  collected.Title == "T" && collected.Selector == "#main",
                "页面引用交给聊天接线并返回成功");

            settings = BrowserSettings.Default with { DefaultBrowsePermission = "block" };
            var blocked = server.ExecuteAsync("navigate", "{\"url\":\"https://blocked.example/\"}").GetAwaiter().GetResult();
            using var blockedDoc = JsonDocument.Parse(blocked);
            check(blockedDoc.RootElement.TryGetProperty("error", out var blockedError) &&
                  (blockedError.GetString() ?? string.Empty).Contains("禁止访问", StringComparison.Ordinal),
                "站点权限为禁止时外部动作被真实拒绝");

            var stopped = Send(http, HttpMethod.Get, "/api/status", null, null);
            check(stopped.Status == HttpStatusCode.OK, "停止前服务仍可查询");
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
            check(!server.Running && server.Port == 0, "停止后释放端口并报告 Stopped");
        }
    }

    private static JsonElement GetJson(HttpClient http, string path, string? token)
    {
        var response = Send(http, HttpMethod.Get, path, null, token);
        if (string.IsNullOrWhiteSpace(response.Body)) throw new InvalidDataException("空响应：" + path);
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.Clone();
    }

    private static (HttpStatusCode Status, string Body) Send(HttpClient http, HttpMethod method,
        string path, string? body, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = http.Send(request);
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return (response.StatusCode, reader.ReadToEnd());
    }
}
