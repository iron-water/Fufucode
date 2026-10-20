using System.Threading;

namespace Fufucode.Core.Diagnostics.Perf;

/// <summary>宿主 → 插件的性能样本（由宿主采集器周期发布，插件订阅后落盘并在页面展示）。</summary>
public sealed record PerfHostSample(DateTimeOffset TimestampUtc, PerfHostSampleKind Kind, string Name, double Value,
    long? InputTokens = null, long? OutputTokens = null, string? Detail = null);

public enum PerfHostSampleKind
{
    /// <summary>一个回合结束：Value = 耗时毫秒，Detail 描述消息/工具数量。</summary>
    Turn = 0,

    /// <summary>运行期宿主指标：Name 区分 workingSetMb / cpuPercent / uiBatchMs。</summary>
    Runtime = 1,
}

/// <summary>接收宿主样本的实现（插件）。</summary>
public interface IPerfHostSampleSink
{
    void Publish(PerfHostSample sample);
}

/// <summary>
/// 宿主采集器的静态发布点：每个窗口各自采集，但只发布给进程唯一的订阅方（插件）。
/// 未订阅时发布为一次 null 判断。
/// </summary>
public static class PerfHostSamples
{
    private static IPerfHostSampleSink? s_sink;

    public static IPerfHostSampleSink? Sink => Volatile.Read(ref s_sink);

    public static void Subscribe(IPerfHostSampleSink? sink) => Volatile.Write(ref s_sink, sink);

    public static void Publish(PerfHostSample sample) => Sink?.Publish(sample);
}

/// <summary>宿主样本的 JSON 序列化上下文（源生成，兼容裁剪发布）。</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PerfHostSample))]
public partial class PerfHostSampleJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
