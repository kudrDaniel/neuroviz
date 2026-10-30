using System.Buffers.Binary;

namespace NeuroViz.Core.Ipc;

public static class PipeProtocol
{
    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header, ct);
        if (read == 0) return null;             // EOF
        while (read < 4)
        {
            var more = await stream.ReadAsync(header.AsMemory(read, 4 - read), ct);
            if (more == 0) return null;
            read += more;
        }
        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        var buf = new byte[len];
        var total = 0;
        while (total < len)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total, len - total), ct);
            if (n == 0) return null;
            total += n;
        }
        return buf;
    }
}