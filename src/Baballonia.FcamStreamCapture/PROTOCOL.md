# FCAM: face camera stream over UDP

FCAM is a small datagram protocol that carries JPEG frames from a face tracker
attached to one machine to Baballonia running on another. It was designed for a
Babble tracker plugged into the USB-C port of a Steam Frame, where the stock
kernel has no USB video class driver and Wi-Fi is the only practical link to the
PC, but any sender that implements this document works.

Design goals:

- **Push, not pull.** UDP datagrams with no connection to keep alive. A lost
  datagram costs one frame, never a stall.
- **No firewall rule on the PC.** Baballonia sends a `SUBSCRIBE` datagram first,
  so the sender's frames arrive as replies to an outbound datagram and stateful
  firewalls (including Windows Defender Firewall) let them in.
- **Easy to implement.** A sender needs only UDP sockets and a byte packer; a
  complete sender fits in a few hundred lines of Python with the standard library.
- **Self-describing chunks.** Every datagram carries the frame length and its
  own byte offset, so the receiver reassembles frames out of order and drops
  incomplete ones without extra state.

Transport: UDP. Default port **8555**. Byte order: big-endian (network order).

## Datagram header (28 bytes)

Every datagram, in both directions, starts with this header.

| Offset | Size | Field          | Description |
|-------:|-----:|----------------|-------------|
| 0      | 4    | `magic`        | ASCII `FCAM` |
| 4      | 1    | `version`      | `1` |
| 5      | 1    | `type`         | `1` FRAME, `2` STATUS, `3` SUBSCRIBE, `4` UNSUBSCRIBE |
| 6      | 1    | `codec`        | `1` JPEG for FRAME, `0` otherwise |
| 7      | 1    | `flags`        | STATUS: bit 0 = a source device is present. Otherwise 0. |
| 8      | 2    | `frame_seq`    | Frame counter, wraps at 65536. All chunks of one frame share it. |
| 10     | 2    | `chunk_index`  | 0-based index of this chunk within the frame |
| 12     | 2    | `chunk_count`  | Number of chunks in the frame (at least 1 for FRAME) |
| 14     | 2    | `payload_len`  | Number of payload bytes that follow the header |
| 16     | 4    | `frame_len`    | Total length of the frame in bytes |
| 20     | 4    | `chunk_offset` | Byte offset of this chunk's payload inside the frame |
| 24     | 4    | `timestamp_ms` | Sender monotonic clock in milliseconds, wraps at 2^32 |

Python `struct` format: `>4sBBBBHHHHIII`. C#: `FcamHeader` in `FcamProtocol.cs`.

A receiver ignores any datagram whose magic or version does not match, whose
`payload_len` does not equal the number of bytes after the header, or whose
`type` it does not know.

## Message types

### FRAME (sender to receiver)

One JPEG frame split into `chunk_count` datagrams. The sender uses a fixed
payload size (1400 bytes recommended, so a datagram stays under a 1500-byte MTU)
for every chunk but the last. The receiver:

1. starts a new frame when it sees a `frame_seq` it is not currently assembling;
   a frame still incomplete at that point is dropped;
2. copies each payload to `chunk_offset`, ignoring duplicates and chunks whose
   offset or length exceeds `frame_len`;
3. decodes the frame once all `chunk_count` chunks arrived, and validates the
   JPEG by decoding it.

Chunks of a `frame_seq` older than the one being assembled (in modular
arithmetic) are ignored.

### STATUS (sender to receiver)

Sent about once per second to every subscriber. `payload` is UTF-8 text of
`key=value` pairs separated by `;`, for example:

```
state=streaming;source=/dev/ttyACM0;fps=44.8;frames=12034;bad=0;dropped=0;subs=1;uptime=812
```

`state` is one of `streaming`, `opening`, `no-source`, `stopping`. Receivers
should show the state to the user but must not rely on any particular key.
`flags` bit 0 mirrors `state=streaming`.

### SUBSCRIBE (receiver to sender)

Sent by the receiver once per second while it wants frames. The sender records
the datagram's source address and streams to it until 3 seconds pass without a
new SUBSCRIBE. `payload` may carry a UTF-8 client name for logs. The sender
must answer from the same socket it received the SUBSCRIBE on, so that
stateful firewalls on the receiver side accept the frames as replies.

### UNSUBSCRIBE (receiver to sender)

Best-effort notice that the receiver is stopping. The sender removes the
subscriber immediately instead of waiting for the timeout.

## Addresses in Baballonia

The `Baballonia.FcamStreamCapture` module claims camera addresses that start
with `fcam://`:

| Address                     | Behaviour |
|-----------------------------|-----------|
| `fcam://192.0.2.10:8555`    | Subscribe mode. Baballonia binds an ephemeral UDP port, sends SUBSCRIBE to the sender every second and receives frames on that port. |
| `fcam://192.0.2.10`         | Same, default port 8555. |
| `fcam://headset.local:8555` | Host names are resolved when capture starts. |
| `fcam://:8555`              | Listen-only mode on UDP 8555, for senders that push to a fixed address. Needs an inbound firewall rule on the PC. |

## Serial framing of Babble/OpenIris trackers (for senders)

A sender that reads a Babble tracker over USB serial sees the OpenIris framing:

```
FF A0 FF A1  <u16 little-endian length>  <JPEG bytes: FF D8 ... FF D9>
```

Firmware log lines are written to the same port between frames. Locate the
4-byte header, read the length, and accept the frame only if the payload begins
with the JPEG SOI marker and ends with the EOI marker; otherwise resynchronise
on the next header. Scanning for `FF D9` alone is not safe: the ESP32 encoder's
JPEG tables can contain that byte pair inside a frame.

## Bandwidth

A 240x240 tracker frame is 2 to 8 KB, so 45 fps is 0.1 to 0.4 MB/s on the
wire (2 to 6 datagrams per frame). Added latency is roughly one frame time.
