using System;
using System.Net;
using System.Text.Json;

namespace Fufucode.Core.Backend;

/// <summary>
/// 模型客户端失败类别。用于把 HTTP 状态与 OpenAI 错误体映射为可判定的语义，
/// 而不是把所有失败都压成一条字符串。
/// </summary>
public enum ModelClientErrorKind
{
    /// <summary>无法分类的失败。</summary>
    Unknown,

    /// <summary>鉴权失败（HTTP 401 / 403）。</summary>
    Authentication,

    /// <summary>限流（HTTP 429）。</summary>
    RateLimited,

    /// <summary>服务端错误（HTTP 5xx）。</summary>
    ServerError,

    /// <summary>请求不合法（其余 4xx）。</summary>
    InvalidRequest,

    /// <summary>流内错误对象（SSE data 携带 error 字段）。</summary>
    StreamError,

    /// <summary>传输层失败（连接中断、DNS、超时等）。</summary>
    Transport,
}

/// <summary>
/// 模型请求失败的结构化异常：保留状态码、错误类别、OpenAI error.code/error.message 与原始错误体。
/// </summary>
public sealed class ModelClientException : Exception
{
    public ModelClientException(
        ModelClientErrorKind kind,
        string message,
        int? statusCode = null,
        string? errorCode = null,
        string? errorBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorBody = errorBody;
    }

    /// <summary>失败类别。</summary>
    public ModelClientErrorKind Kind { get; }

    /// <summary>HTTP 状态码；纯流内错误或无响应时为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>OpenAI 错误体中的 error.code（存在时）。</summary>
    public string? ErrorCode { get; }

    /// <summary>原始错误响应体（已限长，便于诊断且不无限增长）。</summary>
    public string? ErrorBody { get; }

    /// <summary>是否为可重试类别（限流与服务端错误）。</summary>
    public bool IsRetryable => Kind is ModelClientErrorKind.RateLimited or ModelClientErrorKind.ServerError;
}

/// <summary>OpenAI 兼容错误映射：HTTP 状态码与标准 error 对象的统一解析入口。</summary>
public static class ModelClientErrorMapper
{
    /// <summary>错误体保留上限，避免把巨大响应体整段带进异常消息。</summary>
    public const int MaxErrorBodyLength = 2048;

    /// <summary>按 HTTP 状态码映射失败类别。</summary>
    public static ModelClientErrorKind KindFromStatus(int statusCode) => statusCode switch
    {
        401 or 403 => ModelClientErrorKind.Authentication,
        429 => ModelClientErrorKind.RateLimited,
        >= 500 => ModelClientErrorKind.ServerError,
        >= 400 => ModelClientErrorKind.InvalidRequest,
        _ => ModelClientErrorKind.Unknown,
    };

    /// <summary>按 HTTP 状态码与响应体构造结构化异常。</summary>
    public static ModelClientException FromStatus(HttpStatusCode statusCode, string? responseBody)
    {
        var code = (int)statusCode;
        var kind = KindFromStatus(code);
        var body = Bound(responseBody);
        var (errorCode, errorMessage) = ParseErrorPayload(body);

        var reason = string.IsNullOrWhiteSpace(statusCode.ToString())
            ? $"HTTP {code}"
            : $"HTTP {code} {statusCode}";
        var detail = string.IsNullOrWhiteSpace(errorMessage)
            ? body
            : errorMessage;
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"模型请求失败（{reason}）。"
            : $"模型请求失败（{reason}）：{detail}";

        return new ModelClientException(kind, message, code, errorCode, body);
    }

    /// <summary>从流内错误对象构造结构化异常（SSE data 中的 error 字段）。</summary>
    public static ModelClientException FromStreamError(JsonElement errorElement, int? statusCode = null)
    {
        var (errorCode, errorMessage) = ReadErrorElement(errorElement);
        var body = Bound(errorElement.GetRawText());
        var detail = string.IsNullOrWhiteSpace(errorMessage) ? body : errorMessage;
        var message = string.IsNullOrWhiteSpace(detail)
            ? "模型流式响应返回错误对象。"
            : $"模型流式响应错误：{detail}";

        var kind = statusCode is { } status && KindFromStatus(status) != ModelClientErrorKind.Unknown
            ? KindFromStatus(status)
            : ModelClientErrorKind.StreamError;
        return new ModelClientException(kind, message, statusCode, errorCode, body);
    }

    /// <summary>从 OpenAI 标准错误响应体提取 error.message（无则返回 null）。</summary>
    public static string? TryExtractErrorMessage(string? responseBody)
    {
        var (_, message) = ParseErrorPayload(responseBody);
        return message;
    }

    /// <summary>从任意异常构造传输层失败；已是结构化异常时原样返回。</summary>
    public static ModelClientException FromTransport(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ModelClientException mapped) return mapped;

        return new ModelClientException(
            ModelClientErrorKind.Transport,
            $"模型传输失败：{exception.Message}",
            innerException: exception);
    }

    private static (string? Code, string? Message) ParseErrorPayload(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object) return ReadErrorElement(error);
                if (error.ValueKind == JsonValueKind.String) return (null, error.GetString());
            }

            // 部分网关把 message/code 平铺在根节点。
            return ReadErrorElement(root);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Code, string? Message) ReadErrorElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return (null, null);

        string? code = null;
        if (element.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
        {
            code = codeElement.GetString();
        }

        string? message = null;
        if (element.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
        }

        return (code, message);
    }

    private static string? Bound(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        return body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength] + "…";
    }
}
