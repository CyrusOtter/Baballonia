namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Reassembles FRAME chunks into complete frames. Chunks may arrive in any order; a frame that is
/// still incomplete when a newer frame starts is dropped. Not thread safe: the receive loop owns it.
/// </summary>
public sealed class FcamFrameAssembler
{
    /// <summary>
    /// A frame up to this many sequence numbers behind the newest one is a late straggler and is ignored.
    /// A frame further behind means the sender restarted its counter (a bridge restart starts again at 0),
    /// so the assembler forgets the old sequence and accepts it.
    /// </summary>
    public const int ReorderWindow = 32;

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

    /// <summary>How often the sender's frame counter jumped back further than <see cref="ReorderWindow"/>.</summary>
    public long Resyncs { get; private set; }

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
                var delta = SeqDelta(header.FrameSeq, _seq);
                if (delta is < 0 and >= -ReorderWindow)
                {
                    RejectedChunks++; // a straggler from an older frame
                    return null;
                }

                if (delta < 0) Resyncs++;
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
            if (_hasCompleted)
            {
                var delta = SeqDelta(header.FrameSeq, _lastCompletedSeq);
                if (delta is <= 0 and >= -ReorderWindow)
                    return null; // late duplicate of a frame that already completed, or older than it

                if (delta < 0) Resyncs++;
            }

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

    /// <summary>
    /// Forgets any partially received frame and the sequence seen so far, so the next chunk starts a new
    /// stream. Used when the capture stops or reconnects.
    /// </summary>
    public void Reset()
    {
        if (_active) DroppedFrames++;
        _active = false;
        _hasCompleted = false;
        _buffer = null;
        _received = null;
        _receivedCount = 0;
    }

    /// <summary>Signed distance from <paramref name="reference"/> to <paramref name="seq"/> on the 16-bit ring.</summary>
    private static int SeqDelta(ushort seq, ushort reference) => (short)(seq - reference);

    private void Begin(in FcamHeader header)
    {
        _active = true;
        _seq = header.FrameSeq;
        _buffer = new byte[header.FrameLength];
        _received = new bool[header.ChunkCount];
        _receivedCount = 0;
    }
}
