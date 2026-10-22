using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Plugins;

/// <summary>
/// FUFU 命名管道客户端传输（在宿主程序集内，依赖 System.IO.Pipes 由发布根引用保留）。
/// 只做连接与 JSONL 读写；协议分发由调用方（service.fufu-bridge 插件）负责。
/// </summary>
public sealed class FufuPipeClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private NamedPipeClientStream? _pipeIn;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>连接 FUFU 服务端：正向管道（写）+ 反向管道（读），各走单向避免单句柄读写互锁。</summary>
    public async Task ConnectAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", FufuBridgeProtocol.PipeName,
            PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        var pipeIn = new NamedPipeClientStream(".", FufuBridgeProtocol.PipeNameBack,
            PipeDirection.In, PipeOptions.Asynchronous);
        try { await pipeIn.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false); }
        catch { pipe.Dispose(); throw; }
        _pipe = pipe;
        _pipeIn = pipeIn;
        _writer = new StreamWriter(pipe) { AutoFlush = true };
        _reader = new StreamReader(pipeIn);
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_reader is null) throw new InvalidOperationException("未连接。");
        return _reader.ReadLineAsync(cancellationToken).AsTask();
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_writer is null) throw new InvalidOperationException("未连接。");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(line).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        // 断管时 StreamWriter.Dispose 冲刷缓冲会抛 IOException；逐个容忍，重连循环不能被处置异常杀死。
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { _pipeIn?.Dispose(); } catch { }
        _pipe = null; _pipeIn = null; _reader = null; _writer = null;
    }
}
