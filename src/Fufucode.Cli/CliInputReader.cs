using System.Threading.Channels;

namespace Fufucode.Cli;

/// <summary>
/// Console.In may perform a synchronous ReadLine even through its async API.
/// One background reader owns stdin for the whole process; cancellation only stops waiting on its queue.
/// </summary>
public sealed class CliInputReader : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int _disposed;

    public CliInputReader(TextReader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0 && source.ReadLine() is { } line)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    _lines.Writer.WriteAsync(line).AsTask().GetAwaiter().GetResult();
                }
                _lines.Writer.TryComplete();
            }
            catch (ChannelClosedException) { }
            catch (Exception error) { _lines.Writer.TryComplete(error); }
        }) { IsBackground = true, Name = "Fufucode CLI stdin" }.Start();
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (await _lines.Reader.WaitToReadAsync(cancellationToken))
            if (_lines.Reader.TryRead(out var line)) return line;
        return null;
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Interlocked.Exchange(ref _disposed, 1); _lines.Writer.TryComplete(); }
        // Do not close Console.In or wait on a native terminal read. This background thread cannot hold process exit.
        base.Dispose(disposing);
    }
}
