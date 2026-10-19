using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Backend.Tests;

/// <summary>断言失败时抛出，供独立测试入口统一统计通过/失败。</summary>
internal sealed class CheckFailed(string name) : Exception("FAIL " + name);

/// <summary>断言收集器：每条断言打印 PASS 并计数，失败立即抛出。</summary>
internal sealed class Checks
{
    public int Passed { get; private set; }

    public void Check(bool condition, string name)
    {
        if (!condition) throw new CheckFailed(name);
        Passed++;
        Console.WriteLine("PASS " + name);
    }

    public void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailed($"{name}（期望 {expected}，实际 {actual}）");
        Passed++;
        Console.WriteLine("PASS " + name);
    }

    public void Throws<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException)
        {
            Passed++;
            Console.WriteLine("PASS " + name);
            return;
        }
        throw new CheckFailed(name + "（未抛出预期异常）");
    }

    public async Task ThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); }
        catch (TException)
        {
            Passed++;
            Console.WriteLine("PASS " + name);
            return;
        }
        throw new CheckFailed(name + "（未抛出预期异常）");
    }

    /// <summary>
    /// 捕获预期异常并返回实例，便于对异常字段（Kind/StatusCode/ErrorCode）做进一步断言。
    /// 未抛出时返回 null，由调用方断言并给出失败描述（不在此处计数）。
    /// </summary>
    public async Task<TException?> CapturesAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        return null;
    }
}

/// <summary>可脚本化的运行时探测替身，不访问任何真实文件系统。</summary>
internal sealed class FakeRuntimeProbe : IBackendRuntimeProbe
{
    private readonly Dictionary<EngineId, BackendRuntime> _map = new();

    public FakeRuntimeProbe Set(EngineId id, bool available, string? path = null, string? version = null)
    {
        _map[id] = new BackendRuntime(available, path, version);
        return this;
    }

    public BackendRuntime Probe(EngineId id) =>
        _map.TryGetValue(id, out var runtime) ? runtime : new BackendRuntime(false, null, null);
}

/// <summary>纯 C# 内核的最小测试实现：回显脚本化的模型响应并记录工具调度。</summary>
internal sealed class FakePureEngine : IPureCSharpAgentEngine
{
    private PureAgentState _state = new(null, 0, false);

    public List<PureToolCall> Dispatched { get; } = new();

    public Func<PureModelRequest, PureModelResponse>? Model { get; set; }

    public Task<PureAgentTurnResult> RunTurnAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = _state with { IsRunning = true, MessageCount = request.Messages.Count };
        var response = Model?.Invoke(request) ?? PureModelResponse.FromText("done");
        var results = new List<PureToolResult>();
        foreach (var call in response.ToolCalls)
        {
            Dispatched.Add(call);
            results.Add(new PureToolResult(call.Id, true, "ok"));
        }

        _state = _state with { IsRunning = false };
        var status = response.ToolCalls.Count > 0 ? PureAgentTurnStatus.ToolCalls : PureAgentTurnStatus.Completed;
        return Task.FromResult(new PureAgentTurnResult(status, response, results));
    }

    public PureAgentState GetState() => _state;

    public void RestoreHistory(IReadOnlyList<PureAgentMessage> messages)
    {
        _state = _state with { MessageCount = messages.Count, History = messages };
    }

    public void Reset() => _state = new PureAgentState(null, 0, false);
}

/// <summary>测试用脚本化模型：首轮请求 read_file，收到工具结果后回显其内容。</summary>
internal sealed class ScriptedToolEchoClient : IPureModelClient
{
    public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var last = request.Messages.Count > 0 ? request.Messages[^1] : null;
        if (last?.Role == PureAgentRole.Tool)
            return Task.FromResult(PureModelResponse.FromText(last.Content));

        var user = request.Messages.LastOrDefault(m => m.Role == PureAgentRole.User)?.Content ?? string.Empty;
        if (user.Contains("读取") && request.ToolNames.Contains("read_file"))
        {
            return Task.FromResult(PureModelResponse.FromToolCalls(
                new[] { new PureToolCall("call_read_1", "read_file", "{\"path\":\"sample.txt\"}") }));
        }
        return Task.FromResult(PureModelResponse.FromText("完成"));
    }
}

/// <summary>测试用固定文本模型。</summary>
internal sealed class FixedTextClient(string text) : IPureModelClient
{
    public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(PureModelResponse.FromText(text));
}

/// <summary>测试用阻塞模型：请求发出后一直等待取消，用于验证队列/停止语义。</summary>
internal sealed class BlockingTextClient : IPureModelClient
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return PureModelResponse.FromText("unreachable");
    }
}

/// <summary>测试用阻塞工具：一直等待取消，用于验证取消时 tool_result 配对。</summary>
internal sealed class CancelBlockingDispatcher : IPureToolDispatcher
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<PureToolResult> DispatchAsync(PureToolCall call,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new PureToolResult(call.Id, true, "unreachable");
    }
}

/// <summary>测试用固定工具调用模型：首轮发起指定工具，收到工具结果后返回固定文本。</summary>
internal sealed class FixedToolCallClient(PureToolCall call, string finalText = "done") : IPureModelClient
{
    public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var last = request.Messages.Count > 0 ? request.Messages[^1] : null;
        return Task.FromResult(last?.Role == PureAgentRole.Tool
            ? PureModelResponse.FromText(finalText)
            : PureModelResponse.FromToolCalls(new[] { call }));
    }
}

/// <summary>测试用插件调度器：声明一个工具并返回固定结果，用于验证组合路由。</summary>
internal sealed class FakePluginDispatcher(string toolName, string result) : IPureToolDispatcher, IPluginToolCatalog
{
    public IReadOnlyList<string> ActiveToolNames => new[] { toolName };
    public IReadOnlyList<PluginToolRegistration> ActiveRegistrations => new[]
    {
        new PluginToolRegistration(toolName, "fake plugin tool", "{\"type\":\"object\"}", PluginCapability.BrowserControl)
    };

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PureToolResult(call.Id, true, result));
}

/// <summary>测试用捕获模型：记录每次请求，返回固定文本。</summary>
internal sealed class CapturingTextClient(string text) : IPureModelClient
{
    public List<PureModelRequest> Requests { get; } = new();

    public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 快照：引擎会在请求之后继续向同一历史列表追加消息。
        Requests.Add(new PureModelRequest(request.Messages.ToArray(), request.ToolNames.ToArray(),
            request.ModelId, request.ToolDefinitions, request.ReasoningEffort));
        return Task.FromResult(PureModelResponse.FromText(text));
    }
}

/// <summary>记录工具调用并返回成功的调度器（子代理测试）。</summary>
internal sealed class RecordingDispatcher : IPureToolDispatcher
{
    public List<PureToolCall> Calls { get; } = new();

    public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
    {
        Calls.Add(call);
        return Task.FromResult(new PureToolResult(call.Id, true, "recorded:" + call.Name));
    }
}
