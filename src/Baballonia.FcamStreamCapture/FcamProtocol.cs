using System.Buffers.Binary;

namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Message types of the FCAM/UDP protocol. See PROTOCOL.md in this folder for the wire format.
/// </summary>
public enum FcamMessageType : byte
{
    Frame = 1,
    Status = 2,
    Subscribe = 3,
    Unsubscribe = 4,
}

public static class FcamProtocol
{
    public const string Scheme = "fcam://";
    public const int DefaultPort = 8555;
    public const int HeaderLength = 28;
    public const byte Version = 1;
    public const byte CodecNone = 0;
    public const byte CodecJpeg = 1;
    public const byte FlagSourcePresent = 0x01;
    public const int DefaultChunkSize = 1400;
    public const int MaxFrameLength = 4 * 1024 * 1024;
    public const int MaxDatagramLength = 65535;

    public static ReadOnlySpan<byte> Magic => "FCAM"u8;
}

/// <summary>
/// The 28-byte header that starts every FCAM datagram. All integers are big-endian.
/// </summary>
public readonly record struct FcamHeader(
    FcamMessageType Type,
    byte Codec,
    byte Flags,
    ushort FrameSeq,
    ushort ChunkIndex,
    ushort ChunkCount,
    ushort PayloadLength,
    uint FrameLength,
    uint ChunkOffset,
    uint TimestampMs)
{
    public static bool TryParse(ReadOnlySpan<byte> data, out FcamHeader header)
    {
        header = default;
        if (data.Length < FcamProtocol.HeaderLength) return false;
        if (!data[..4].SequenceEqual(FcamProtocol.Magic)) return false;
        if (data[4] != FcamProtocol.Version) return false;
        var type = data[5];
        if (type is < (byte)FcamMessageType.Frame or > (byte)FcamMessageType.Unsubscribe) return false;

        header = new FcamHeader(
            (FcamMessageType)type,
            data[6],
            data[7],
            BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[14..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[24..]));
        return true;
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < FcamProtocol.HeaderLength)
            throw new ArgumentException("Destination is smaller than an FCAM header", nameof(destination));

        FcamProtocol.Magic.CopyTo(destination);
        destination[4] = FcamProtocol.Version;
        destination[5] = (byte)Type;
        destination[6] = Codec;
        destination[7] = Flags;
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], FrameSeq);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], ChunkIndex);
        BinaryPrimitives.WriteUInt16BigEndian(destination[12..], ChunkCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], FrameLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], ChunkOffset);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], TimestampMs);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[FcamProtocol.HeaderLength];
        Write(bytes);
        return bytes;
    }

    /// <summary>
    /// Splits one encoded frame into FRAME datagrams the same way the bridge does.
    /// Used by tests and tools; the capture only receives.
    /// </summary>
    public static IEnumerable<byte[]> ChunkFrame(ReadOnlyMemory<byte> frame, ushort seq,
        int chunkSize = FcamProtocol.DefaultChunkSize, uint timestampMs = 0, byte codec = FcamProtocol.CodecJpeg)
    {
        if (chunkSize <= 0 || chunkSize > FcamProtocol.MaxDatagramLength - FcamProtocol.HeaderLength)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var total = frame.Length;
        var count = Math.Max(1, (total + chunkSize - 1) / chunkSize);
        if (count > ushort.MaxValue) throw new ArgumentException("Frame needs more than 65535 chunks", nameof(frame));

        for (var index = 0; index < count; index++)
        {
            var offset = index * chunkSize;
            var length = Math.Min(chunkSize, total - offset);
            var datagram = new byte[FcamProtocol.HeaderLength + length];
            new FcamHeader(FcamMessageType.Frame, codec, 0, seq, (ushort)index, (ushort)count, (ushort)length,
                (uint)total, (uint)offset, timestampMs).Write(datagram);
            frame.Slice(offset, length).CopyTo(datagram.AsMemory(FcamProtocol.HeaderLength));
            yield return datagram;
        }
    }

    /// <summary>Builds a STATUS, SUBSCRIBE or UNSUBSCRIBE datagram with an optional text payload.</summary>
    public static byte[] Control(FcamMessageType type, ReadOnlySpan<byte> payload = default, uint timestampMs = 0,
        byte flags = 0)
    {
        var datagram = new byte[FcamProtocol.HeaderLength + payload.Length];
        new FcamHeader(type, FcamProtocol.CodecNone, flags, 0, 0, 0, (ushort)payload.Length, 0, 0, timestampMs)
            .Write(datagram);
        payload.CopyTo(datagram.AsSpan(FcamProtocol.HeaderLength));
        return datagram;
    }
}
