using System;
using System.IO;
using System.Linq;
using System.Text;
using Fufucode.Core.Plugins;

internal static class NvxProtocolTests
{
    public static void Run()
    {
        var instance = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("hello nvx");
        var encoded = NvxRecordCodec.Encode(NvxRecordType.Data, instance, 7, 3, payload);
        var decoded = NvxRecordCodec.Decode(encoded);
        Check(decoded.Type == NvxRecordType.Data && decoded.InstanceId == instance &&
            decoded.Epoch == 7 && decoded.Sequence == 3 && decoded.Payload.SequenceEqual(payload),
            "NVXS record roundtrip preserves identity and payload");

        var parser = new NvxRecordParser();
        var split = encoded.Length / 2;
        Check(!parser.TryParse(encoded.AsSpan(0, split), out _, out _),
            "NVXS parser waits for a complete record");
        Check(parser.TryParse(encoded.AsSpan(split), out _, out var parsed) && parsed is not null &&
            parsed.Payload.SequenceEqual(payload), "NVXS parser accepts a record split across reads");

        var second = NvxRecordCodec.Encode(NvxRecordType.Data, instance, 7, 4, "second"u8);
        var combined = encoded.Concat(second).ToArray();
        var multi = new NvxRecordParser();
        Check(multi.TryParse(combined, out _, out var firstRecord) && firstRecord is not null &&
            firstRecord.Sequence == 3, "NVXS parser returns the first record from a combined read");
        Check(multi.TryParse(ReadOnlySpan<byte>.Empty, out _, out var secondRecord) && secondRecord is not null &&
            secondRecord.Sequence == 4, "NVXS parser retains the next combined record");

        var app = NvxAppFrameCodec.Encode(NvxAppFrameType.Ready, 9, 0, "ready"u8);
        var appDecoded = NvxAppFrameCodec.Decode(app);
        Check(appDecoded.Type == NvxAppFrameType.Ready && appDecoded.RequestId == 9 &&
            Encoding.UTF8.GetString(appDecoded.Payload) == "ready", "NVXC frame roundtrip preserves request metadata");

        var invalid = encoded.ToArray();
        invalid[0] = (byte)'X';
        try
        {
            NvxRecordCodec.Decode(invalid);
            throw new Exception("invalid NVXS magic was accepted");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS invalid NVXS magic is rejected");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
}
