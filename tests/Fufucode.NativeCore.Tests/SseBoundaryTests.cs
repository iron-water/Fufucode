using System.Text;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 12: decoder must hold a full 4096-byte chunk's UTF-16 output, split CR/LF included.</summary>
internal static class SseBoundaryTests
{
    public static async Task RunAsync(Checks checks)
    {
        var utf8 = new UTF8Encoding(false, false);
        var scalar = utf8.GetBytes("\U0001F600");
        var continuation = new[] { scalar[3] }.Concat(Encoding.ASCII.GetBytes(new string('a', 4095))).ToArray();
        checks.Equal(4096, continuation.Length, "second chunk is exactly 4096 bytes");

        // Negative control: the old 4096-char allocation cannot hold 2 surrogates + 4095 ASCII.
        var oldDecoder = utf8.GetDecoder();
        oldDecoder.GetChars(scalar, 0, 3, new char[4096], 0, false);
        try
        {
            oldDecoder.GetChars(continuation, 0, continuation.Length, new char[4096], 0, false);
            throw new InvalidOperationException("FAIL old 4096-char allocation accepted the counterexample");
        }
        catch (ArgumentException)
        {
            checks.Check(true, "negative control: old 4096-char allocation throws");
        }

        var decoder = utf8.GetDecoder();
        var chars = new char[utf8.GetMaxCharCount(4096)];
        checks.Equal(0, decoder.GetChars(scalar, 0, 3, chars, 0, false), "split scalar emits no chars in first chunk");
        var count = decoder.GetChars(continuation, 0, continuation.Length, chars, 0, false);
        var expected = "\U0001F600" + new string('a', 4095);
        checks.Equal(4097, count, "4096 input bytes produce 4097 UTF-16 chars");
        checks.Equal(expected, new string(chars, 0, count), "decoded text is exact");

        foreach (var ending in new[] { "\n", "\r\n", "\r" })
        {
            var first = Encoding.ASCII.GetBytes("data: ").Concat(scalar.Take(3)).ToArray();
            var chunks = new List<byte[]> { first, continuation };
            chunks.AddRange(ending.Select(c => Encoding.ASCII.GetBytes(c.ToString())));
            chunks.AddRange(ending.Select(c => Encoding.ASCII.GetBytes(c.ToString())));
            chunks.Add(Encoding.UTF8.GetBytes("data: 中文\n\ndata: [DONE]\n\n"));
            await using var stream = new ExactChunkStream(chunks.ToArray());
            var events = new List<ServerSentEvent>();
            await foreach (var item in ServerSentEvents.ReadAsync(stream)) events.Add(item);
            checks.Equal(3, events.Count, $"SSE frames for {Display(ending)}");
            checks.Equal(expected, events[0].Data, $"exact first data for {Display(ending)}");
            checks.Equal("中文", events[1].Data, $"second data for {Display(ending)}");
            checks.Check(events[2].IsDone, $"[DONE] for {Display(ending)}");
            checks.Equal(9, stream.ReadSizes[0], $"first read size for {Display(ending)}");
            checks.Equal(4096, stream.ReadSizes[1], $"boundary read size for {Display(ending)}");
        }
    }

    private static string Display(string ending) =>
        ending.Replace("\r", "CR").Replace("\n", "LF");

    /// <summary>Returns each scripted chunk verbatim; never coalesces or re-splits.</summary>
    private sealed class ExactChunkStream(byte[][] chunks) : Stream
    {
        private int _index;
        public List<int> ReadSizes { get; } = [];
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == chunks.Length) return ValueTask.FromResult(0);
            var chunk = chunks[_index++];
            if (chunk.Length > buffer.Length) throw new InvalidOperationException("Read buffer smaller than scripted chunk.");
            chunk.CopyTo(buffer);
            ReadSizes.Add(chunk.Length);
            return ValueTask.FromResult(chunk.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
