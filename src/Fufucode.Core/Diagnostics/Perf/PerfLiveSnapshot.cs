using System;
using System.Collections.Generic;

namespace Fufucode.Core.Diagnostics;

/// <summary>可选的内存实时视图；开始/进度不写入完成记录，也不参加耗时基线。</summary>
public interface IPerfLiveSink
{
    void OnSpanProgress(in PerfSpanStart start, IReadOnlyList<KeyValuePair<string, string>>? tags);
}

/// <summary>宿主补充当前会话及可观察的上下文；历史没有时钟信息时不生成耗时 span。</summary>
public interface IPerfSessionSink
{
    void OnSessionContext(PerfSessionContext context);
}

public sealed record PerfContextPart(string Key, string Name, long Tokens);

public sealed record PerfSessionContext(
    string? SessionId,
    string? SessionName,
    string? Model,
    string? Backend,
    long TimestampTicks,
    IReadOnlyList<PerfContextPart> Parts,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    long? CachedTokens = null,
    bool IsPartial = true);
