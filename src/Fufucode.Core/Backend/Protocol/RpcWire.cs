using Fufucode.Core;
using System;
using System.Text.Json;

namespace Fufucode.Core;

/// <summary>
/// RPC 内核（当前为 Pi/OMP 兼容协议）的通用线路契约。
/// 这些类型属于 Core 的公共面：会话协调器、历史与诊断都按它们编程；
/// 具体进程启动与管道读写由后端插件（Fufucode.Plugins.Pi）实现。
/// </summary>
public sealed record RpcResponse(string Id, string Command, bool Success, JsonElement? Data, string? Error);

public sealed record RpcEvent(Guid Generation, string Type, JsonElement Record);

public sealed record RpcProcessExit(Guid Generation, int ProcessId, int ExitCode, DateTimeOffset ObservedAtUtc);

public sealed class RpcException(string message, Exception? inner = null) : Exception(message, inner);
