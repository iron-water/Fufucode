using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

public enum NvxRecordType : byte
{
    GuestAttach = 1,
    HostAttach = 2,
    Reset = 3,
    Ack = 4,
    Data = 5,
    Wait = 6,
    Ready = 7,
    Error = 8,
    Credit = 9,
}

public sealed record NvxRecord(
    NvxRecordType Type,
    Guid InstanceId,
    ulong Epoch,
    ulong Sequence,
    byte[] Payload);

/// <summary>NVXS outer record codec. Header layout follows microsoft/nvx protocol version 1.</summary>
public static class NvxRecordCodec
{
    public const int HeaderLength = 44;
    public const int CapabilityLength = 32;
    public const int MaxPayloadLength = 1024 * 1024;
    private static readonly byte[] Magic = "NVXS"u8.ToArray();

    public static byte[] Encode(
        NvxRecordType type,
        Guid instanceId,
        ulong epoch,
        ulong sequence,
        ReadOnlySpan<byte> payload)
    {
        Validate(type, instanceId, epoch, sequence, payload.Length);
        var output = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4, 2), 1);
        output[6] = (byte)type;
        output[7] = 0;
        instanceId.TryWriteBytes(output.AsSpan(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(24, 8), epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(32, 8), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(40, 4), checked((uint)payload.Length));
        payload.CopyTo(output.AsSpan(HeaderLength));
        return output;
    }

    public static NvxRecord Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength) throw new InvalidDataException("NVXS record header is truncated.");
        if (!frame[..4].SequenceEqual(Magic)) throw new InvalidDataException("NVXS record magic is invalid.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) != 1) throw new InvalidDataException("NVXS version is unsupported.");
        if (frame[7] != 0) throw new InvalidDataException("NVXS flags must be zero.");
        var type = ParseType(frame[6]);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[40..]));
        if (frame.Length != HeaderLength + length) throw new InvalidDataException("NVXS record length does not match its payload.");
        var instance = new Guid(frame.Slice(8, 16), bigEndian: true);
        var epoch = BinaryPrimitives.ReadUInt64LittleEndian(frame[24..]);
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(frame[32..]);
        Validate(type, instance, epoch, sequence, length);
        return new NvxRecord(type, instance, epoch, sequence, frame[HeaderLength..].ToArray());
    }

    private static NvxRecordType ParseType(byte value) => value switch
    {
        >= (byte)NvxRecordType.GuestAttach and <= (byte)NvxRecordType.Credit => (NvxRecordType)value,
        _ => throw new InvalidDataException($"NVXS record type {value} is unsupported."),
    };

    private static void Validate(NvxRecordType type, Guid instance, ulong epoch, ulong sequence, int length)
    {
        var bootstrap = type is NvxRecordType.GuestAttach or NvxRecordType.HostAttach;
        if (bootstrap && (instance != Guid.Empty || epoch != 0 || sequence != 0))
            throw new InvalidDataException("NVXS bootstrap record must have zero identity and sequence.");
        var validLength = type switch
        {
            NvxRecordType.GuestAttach or NvxRecordType.Reset or NvxRecordType.Wait or NvxRecordType.Ready => length == 0,
            NvxRecordType.HostAttach => length == CapabilityLength,
            NvxRecordType.Ack or NvxRecordType.Error or NvxRecordType.Credit => length == 4,
            NvxRecordType.Data => length is >= 1 and <= MaxPayloadLength,
            _ => false,
        };
        if (!validLength) throw new InvalidDataException($"NVXS {type} payload length {length} is invalid.");
    }
}

/// <summary>Incremental bounded parser. It retains at most one NVXS record.</summary>
public sealed class NvxRecordParser
{
    private byte[] _buffer = Array.Empty<byte>();
    private Exception? _error;

    public int BufferedBytes => _buffer.Length;

    public bool TryParse(ReadOnlySpan<byte> input, out int consumed, out NvxRecord? record)
    {
        if (_error is not null) throw new InvalidDataException("NVXS parser is permanently failed.", _error);
        consumed = 0;
        record = null;
        var bufferedBeforeInput = _buffer.Length;
        if (!input.IsEmpty)
        {
            var merged = new byte[_buffer.Length + input.Length];
            _buffer.CopyTo(merged, 0);
            input.CopyTo(merged.AsSpan(_buffer.Length));
            _buffer = merged;
        }
        try
        {
            if (_buffer.Length < NvxRecordCodec.HeaderLength) return false;
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(40, 4)));
            if (length > NvxRecordCodec.MaxPayloadLength) throw new InvalidDataException("NVXS payload exceeds the limit.");
            var total = NvxRecordCodec.HeaderLength + length;
            if (_buffer.Length < total) return false;
            record = NvxRecordCodec.Decode(_buffer.AsSpan(0, total));
            _buffer = _buffer[total..];
            consumed = Math.Max(0, total - bufferedBeforeInput);
            return true;
        }
        catch (Exception error)
        {
            _error = error;
            _buffer = Array.Empty<byte>();
            throw;
        }
    }
}

public enum NvxAppFrameType : byte
{
    Ping = 1,
    Exec = 2,
    Stop = 3,
    Cancel = 4,
    Features = 5,
    Maps = 6,
    Ready = 0x81,
    Stdout = 0x82,
    Stderr = 0x83,
    Exit = 0x84,
    Stopped = 0x85,
    Error = 0xff,
}

public sealed record NvxAppFrame(NvxAppFrameType Type, ulong RequestId, int Status, byte[] Payload);

public static class NvxAppFrameCodec
{
    public const int HeaderLength = 24;
    public const int MaxPayloadLength = NvxRecordCodec.MaxPayloadLength;

    public static byte[] Encode(NvxAppFrameType type, ulong requestId, int status, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
        var output = new byte[HeaderLength + payload.Length];
        "NVXC"u8.CopyTo(output);
        output[4] = 1;
        output[5] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(8, 8), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16, 4), status);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), checked((uint)payload.Length));
        payload.CopyTo(output.AsSpan(HeaderLength));
        return output;
    }

    public static NvxAppFrame Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength || !frame[..4].SequenceEqual("NVXC"u8) || frame[4] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]) != 0)
            throw new InvalidDataException("NVXC application frame header is invalid.");
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[20..]));
        if (length > MaxPayloadLength || frame.Length != HeaderLength + length)
            throw new InvalidDataException("NVXC application frame length is invalid.");
        return new NvxAppFrame((NvxAppFrameType)frame[5],
            BinaryPrimitives.ReadUInt64LittleEndian(frame[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(frame[16..]),
            frame[HeaderLength..].ToArray());
    }
}

/// <summary>最小 Windows 命名管道传输层；不负责启动 OpenVMM 或决定执行策略。</summary>
public sealed class NvxNamedPipeControlClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly byte[] _capability;
    private NamedPipeClientStream? _stream;
    private Guid _instanceId;
    private ulong _epoch;
    private ulong _sequence;
    private ulong _requestId;

    public NvxNamedPipeControlClient(string pipeName, ReadOnlySpan<byte> capability)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (capability.Length != NvxRecordCodec.CapabilityLength) throw new ArgumentException("NVX capability must be 32 bytes.", nameof(capability));
        _pipeName = NormalizePipeName(pipeName);
        _capability = capability.ToArray();
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NVX named-pipe transport requires Windows.");
        if (_stream is not null) throw new InvalidOperationException("NVX control client is already connected.");
        _stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await _stream.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        await WriteRecordAsync(NvxRecordCodec.Encode(NvxRecordType.HostAttach, Guid.Empty, 0, 0, _capability), timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<NvxRecord> ReadRecordAsync(CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("NVX control client is not connected.");
        var header = new byte[NvxRecordCodec.HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4)));
        if (length > NvxRecordCodec.MaxPayloadLength) throw new InvalidDataException("NVXS payload exceeds the limit.");
        var frame = new byte[NvxRecordCodec.HeaderLength + length];
        header.CopyTo(frame, 0);
        if (length > 0) await ReadExactlyAsync(stream, frame.AsMemory(NvxRecordCodec.HeaderLength, length), cancellationToken).ConfigureAwait(false);
        var record = NvxRecordCodec.Decode(frame);
        if (record.Type == NvxRecordType.Ready)
        {
            if (record.InstanceId == Guid.Empty || record.Epoch == 0)
                throw new InvalidDataException("NVXS READY record has no session identity.");
            _instanceId = record.InstanceId;
            _epoch = record.Epoch;
            _sequence = 0;
        }
        return record;
    }

    public async Task<ulong> SendApplicationFrameAsync(NvxAppFrame frame, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("NVX control client is not connected.");
        var id = frame.RequestId == 0 ? ++_requestId : frame.RequestId;
        var app = NvxAppFrameCodec.Encode(frame.Type, id, frame.Status, frame.Payload);
        if (_instanceId == Guid.Empty || _epoch == 0)
            throw new InvalidOperationException("NVX control client is not ready; read READY before sending application data.");
        var record = NvxRecordCodec.Encode(NvxRecordType.Data, _instanceId, _epoch, _sequence++, app);
        await WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task WriteRecordAsync(byte[] record, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("NVX control client is not connected.");
        await stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("NVX control endpoint closed before the record completed.");
            buffer = buffer[read..];
        }
    }

    private static string NormalizePipeName(string name)
    {
        const string prefix = @"\\.\pipe\";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
    }
}
