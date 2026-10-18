using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Fufucode.App.Features.Automation.Persistence;

/// <summary>
/// 严格 JSON 文本校验：拒绝注释、重复属性名（任意层级）、非法 JSON 与未闭合结构。
/// System.Text.Json 的默认读取会静默取最后一个重复键，因此本模块自己走一遍 reader。
/// </summary>
internal static class AutomationStrictJson
{
    public const int MaxDepth = 64;

    /// <summary>校验文本是合法 JSON 对象/数组且不超过 UTF-8 上限；超限与语法错误分别归类。</summary>
    public static bool IsValidObjectOrArray(string? text, int maxUtf8Bytes, out string error)
        => IsValidObjectOrArray(text, maxUtf8Bytes, out error, out _);

    public static bool IsValidObjectOrArray(string? text, int maxUtf8Bytes, out string error, out AutomationPersistenceErrorCode code)
    {
        error = "";
        code = AutomationPersistenceErrorCode.InvalidFileContent;
        if (text is null) { error = "内容为 null"; return false; }
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > maxUtf8Bytes)
        {
            error = $"内容 {byteCount} 字节超过上限 {maxUtf8Bytes}";
            code = AutomationPersistenceErrorCode.Oversize;
            return false;
        }
        return Validate(Encoding.UTF8.GetBytes(text), out error);
    }

    /// <summary>校验整份文档；要求根值是对象或数组，且没有重复键。</summary>
    public static bool Validate(byte[] utf8, out string error)
    {
        error = "";
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(utf8), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = MaxDepth,
        });

        var memberStack = new Stack<HashSet<string>?>();
        var depth = 0;
        var rootKind = JsonValueKind.Undefined;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        if (depth == 0) rootKind = JsonValueKind.Object;
                        memberStack.Push(new HashSet<string>(StringComparer.Ordinal));
                        depth++;
                        break;
                    case JsonTokenType.StartArray:
                        if (depth == 0) rootKind = JsonValueKind.Array;
                        memberStack.Push(null);
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (depth == 0) { error = "结构在根层级之外闭合"; return false; }
                        memberStack.Pop();
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        if (memberStack.Count == 0 || memberStack.Peek() is null)
                        {
                            error = "属性名出现在数组或根层级之外";
                            return false;
                        }
                        var name = reader.GetString()!;
                        if (!memberStack.Peek()!.Add(name))
                        {
                            error = "JSON 含重复属性名：" + name;
                            return false;
                        }
                        break;
                }
            }
        }
        catch (JsonException jsonError)
        {
            error = "非法 JSON：" + jsonError.Message;
            return false;
        }

        if (depth != 0) { error = "JSON 未闭合"; return false; }
        if (rootKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            error = rootKind == JsonValueKind.Undefined ? "空文档" : "根值必须是对象或数组";
            return false;
        }
        return true;
    }
}
