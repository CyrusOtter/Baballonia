namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Reassembles FRAME chunks into complete frames. Chunks may arrive in any order; a frame that is
/// still incomplete when a newer frame starts is dropped. Not thread safe: the receive loop owns it.
/// </summary>
public sealed class FcamFrameAssembler
{
    private byte[]? _buffer;
    private bool[]? _received;
    private int _receivedCount;
    private ushort _seq;
    private bool _active;
    private bool _hasCompleted;
    private ushort _lastCompletedSeq;

    public long CompletedFrames { get; private set; }
    public long DroppedFrames { get; private set; }
    public long RejectedChunks { get; private set; }

    /// <summary>
    /// Feeds one FRAME chunk. Returns the complete frame when this chunk completed it, otherwise null.
    /// The returned array is owned by the caller.
    /// </summary>
    public byte[]? Add(in FcamHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.Type != FcamMessageType.Frame) return null;

        if (header.ChunkCount == 0 || header.ChunkIndex >= header.ChunkCount ||
            header.FrameLength == 0 || header.FrameLength > FcamProtocol.MaxFrameLength ||
            payload.Length != header.PayloadLength ||
            (long)header.ChunkOffset + payload.Length > header.FrameLength)
        {
            RejectedChunks++;
            return null;
        }

        if (_active)
        {
            if (header.FrameSeq != _seq)
            {
                if ((short)(header.FrameSeq - _seq) < 0)
                {
                    RejectedChunks++; // a straggler from an older frame
                    return null;
                }

                DroppedFrames++; // the frame in progress can no longer complete
                Begin(header);
            }
            else if (_buffer!.Length != header.FrameLength || _received!.Length != header.ChunkCount)
            {
                RejectedChunks++; // inconsistent with the chunks seen so far
                return null;
            }
        }
        else
        {
            if (_hasCompleted && (short)(header.FrameSeq - _lastCompletedSeq) <= 0)
                return null; // late duplicate of a frame that already completed, or older than it

            Begin(header);
        }

        if (_received![header.ChunkIndex]) return null; // duplicate chunk

        payload.CopyTo(_buffer.AsSpan((int)header.ChunkOffset));
        _received[header.ChunkIndex] = true;
        _receivedCount++;
        if (_receivedCount < header.ChunkCount) return null;

        _active = false;
        _hasCompleted = true;
        _lastCompletedSeq = _seq;
        CompletedFrames++;

        var frame = _buffer!;
        _buffer = null;
        _received = null;
        return frame;
    }

    /// <summary>Forgets any partially received frame.</summary>
    public void Reset()
    {
        if (_active) DroppedFrames++;
        _active = false;
        _buffer = null;
        _received = null;
        _receivedCount = 0;
    }

    private void Begin(in FcamHeader header)
    {
        _active = true;
        _seq = header.FrameSeq;
        _buffer = new byte[header.FrameLength];
        _received = new bool[header.ChunkCount];
        _receivedCount = 0;
    }
}
