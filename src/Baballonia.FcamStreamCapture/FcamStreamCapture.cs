using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Capture = Baballonia.SDK.Capture;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Receives JPEG frames over the FCAM/UDP protocol (PROTOCOL.md in this folder), for example from
/// a small bridge on a Steam Frame that forwards a Babble tracker plugged into its USB-C port.
/// <para>
/// With <c>fcam://host:port</c> the capture subscribes to the bridge once a second and receives the
/// frames as replies on an ephemeral port, which passes stateful firewalls without rules.
/// With <c>fcam://:port</c> it only listens on that port for frames pushed by a bridge.
/// </para>
/// <para>
/// The capture reconnects on its own. A bridge that restarts, a network that drops for a while or a host
/// name that now resolves to another address only pause the stream: when the bridge stays silent for
/// <see cref="ReconnectAfterSilence"/>, the capture resolves the host again and subscribes from a new
/// socket, and it keeps doing so until the bridge answers. <see cref="Capture.IsReady"/> stays true from
/// <see cref="StartCapture"/> until <see cref="StopCapture"/>.
/// </para>
/// </summary>
public sealed class FcamStreamCapture : Capture
{
    private static readonly TimeSpan SubscribeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SocketErrorBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] ClientName = "Baballonia"u8.ToArray();

    private readonly FcamAddress _address;
    private readonly FcamFrameAssembler _assembler = new();
    private readonly object _lifecycleLock = new();
    private readonly Stopwatch _statsWatch = new();

    private CancellationTokenSource? _cts;
    private Task? _supervisorTask;

    private long _datagrams;
    private long _framesDecoded;
    private long _decodeFailures;
    private long _statsFrames;
    private long _reconnects;
    private long _lastDatagramTicks;
    private long _lostSinceTicks;
    private long _stallSinceTicks;
    private long _lastChunkTicks;
    private string _lastState = "";
    private EndPoint? _lastSender;

    private sealed record Connection(Socket Socket, IPEndPoint? Remote);

    private enum SessionEnd
    {
        Stopped,
        Silence,
        SocketError,
    }

    public FcamStreamCapture(string source, ILogger<FcamStreamCapture> logger) : base(source, logger)
    {
        if (!FcamAddress.TryParse(source, out var parsed) || parsed is null)
            throw new ArgumentException($"'{source}' is not a valid fcam:// address", nameof(source));
        _address = parsed;
    }

    public FcamAddress Address => _address;
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);
    public long DroppedFrames => _assembler.DroppedFrames;
    public long DecodeFailures => Interlocked.Read(ref _decodeFailures);

    /// <summary>How often the capture opened a new socket because the bridge fell silent or the socket failed.</summary>
    public long Reconnects => Interlocked.Read(ref _reconnects);

    /// <summary>
    /// Subscribe mode: when no datagram arrived from the bridge for this long, resolve the host again and
    /// subscribe from a new socket. Repeats at this interval until the bridge answers.
    /// </summary>
    public TimeSpan ReconnectAfterSilence { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When frame chunks keep arriving but no frame completed for this long, forget the frame sequence
    /// seen so far and start over with the next chunk.
    /// </summary>
    public TimeSpan FrameStallReset { get; init; } = TimeSpan.FromSeconds(2);

    public override Task<bool> StartCapture()
    {
        lock (_lifecycleLock)
        {
            if (_cts is not null) return Task.FromResult(IsReady);

            Connection? first = null;
            try
            {
                IPEndPoint? remote = null;
                var resolved = true;
                if (!_address.IsListenOnly)
                {
                    try
                    {
                        remote = Resolve(_address.Host!, _address.Port);
                    }
                    catch (SocketException ex)
                    {
                        // the host may simply be off; its name resolves once it is back
                        resolved = false;
                        Logger.LogWarning("FCAM: cannot resolve {Host} yet ({Error}), retrying every {Seconds:F0} s",
                            _address.Host, ex.SocketErrorCode, ReconnectAfterSilence.TotalSeconds);
                    }
                }

                if (resolved) first = Open(remote, LogLevel.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "FCAM: failed to start capture for '{Source}'", Source);
                IsReady = false;
                return Task.FromResult(false);
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _statsWatch.Restart();
            _statsFrames = 0;
            Interlocked.Exchange(ref _lostSinceTicks, 0);
            _supervisorTask = Task.Run(() => SuperviseAsync(first, token), CancellationToken.None);
            IsReady = true;
            return Task.FromResult(true);
        }
    }

    public override Task<bool> StopCapture()
    {
        lock (_lifecycleLock)
        {
            IsReady = false;
            if (_cts is null) return Task.FromResult(true);

            Logger.LogDebug(
                "FCAM: stopping ({Frames} frames decoded, {Dropped} dropped, {Failed} undecodable, {Reconnects} reconnects)",
                FramesDecoded, _assembler.DroppedFrames, DecodeFailures, Reconnects);

            var cts = _cts;
            var supervisor = _supervisorTask;
            _cts = null;
            _supervisorTask = null;
            cts.Cancel();
            try
            {
                supervisor?.Wait(StopTimeout);
            }
            catch (Exception)
            {
                // the supervisor ends by cancellation; its exceptions are expected here
            }

            cts.Dispose();
            return Task.FromResult(true);
        }
    }

    public override void Dispose()
    {
        StopCapture();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Owns the socket for the lifetime of the capture: runs one receive session at a time and opens a new
    /// socket whenever a session ends because the bridge fell silent or the socket failed.
    /// </summary>
    private async Task SuperviseAsync(Connection? connection, CancellationToken token)
    {
        var lastRemote = connection?.Remote;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (connection is null)
                {
                    connection = Reopen(lastRemote);
                    if (connection is null)
                    {
                        var retry = _address.IsListenOnly ? SocketErrorBackoff : ReconnectAfterSilence;
                        if (!await DelayAsync(retry, token).ConfigureAwait(false)) break;
                        continue;
                    }

                    lastRemote = connection.Remote ?? lastRemote;
                }

                var end = await RunSessionAsync(connection, token).ConfigureAwait(false);
                connection = null;
                if (end == SessionEnd.Stopped) break;

                Interlocked.Increment(ref _reconnects);
                if (end == SessionEnd.SocketError && !await DelayAsync(SocketErrorBackoff, token).ConfigureAwait(false))
                    break;
            }
        }
        finally
        {
            // a socket opened but never run, for example when the capture stopped right after starting
            connection?.Socket.Dispose();
        }
    }

    private Connection? Reopen(IPEndPoint? lastRemote)
    {
        try
        {
            IPEndPoint? remote = null;
            if (!_address.IsListenOnly)
            {
                remote = Resolve(_address.Host!, _address.Port);
                if (lastRemote is not null && !remote.Equals(lastRemote))
                    Logger.LogInformation("FCAM: {Host} now resolves to {Remote}", _address.Host, remote);
            }

            return Open(remote, lastRemote is null ? LogLevel.Information : LogLevel.Debug);
        }
        catch (SocketException ex)
        {
            Logger.LogDebug("FCAM: reconnecting to {Source} failed: {Error}", Source, ex.SocketErrorCode);
            return null;
        }
    }

    private Connection Open(IPEndPoint? remote, LogLevel level)
    {
        var family = remote?.AddressFamily ?? AddressFamily.InterNetwork;
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (family == AddressFamily.InterNetworkV6) socket.DualMode = true;
            socket.ReceiveBufferSize = 4 * 1024 * 1024;
            DisableIcmpResets(socket);
            var bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            socket.Bind(new IPEndPoint(bindAddress, remote is null ? _address.Port : 0));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        if (remote is null)
            Logger.Log(level, "FCAM: listening for frames on UDP port {Port} ({Source})", localPort, Source);
        else
            Logger.Log(level, "FCAM: subscribing to bridge {Remote} from local UDP port {Port} ({Source})",
                remote, localPort, Source);
        return new Connection(socket, remote);
    }

    /// <summary>
    /// Receives on one socket until the capture stops, the socket fails, or (subscribe mode) the bridge
    /// stays silent for <see cref="ReconnectAfterSilence"/>. Closes the socket before returning.
    /// </summary>
    private async Task<SessionEnd> RunSessionAsync(Connection connection, CancellationToken token)
    {
        var (socket, remote) = connection;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = session.Token;

        // the previous receive loop has ended, so the assembler and stream state are free to reset
        _assembler.Reset();
        _lastState = "";
        var start = Environment.TickCount64;
        _stallSinceTicks = 0;
        _lastChunkTicks = 0;

        var receive = Task.Run(() => ReceiveLoopAsync(socket, sessionToken), CancellationToken.None);
        var keepalive = remote is null
            ? Task.CompletedTask
            : Task.Run(() => KeepaliveLoopAsync(socket, remote, sessionToken), CancellationToken.None);

        var end = SessionEnd.Stopped;
        try
        {
            while (true)
            {
                await Task.WhenAny(receive, Task.Delay(WatchInterval, sessionToken)).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;

                if (receive.IsCompleted)
                {
                    if (receive.IsFaulted)
                        Logger.LogWarning(receive.Exception, "FCAM: receive loop failed, reopening the socket");
                    end = SessionEnd.SocketError;
                    break;
                }

                if (remote is null) continue;
                var now = Environment.TickCount64;
                var silentMs = now - Math.Max(Interlocked.Read(ref _lastDatagramTicks), start);
                if (silentMs < ReconnectAfterSilence.TotalMilliseconds) continue;

                if (Interlocked.CompareExchange(ref _lostSinceTicks, Math.Max(1, now - silentMs), 0) == 0)
                    Logger.LogWarning("FCAM: no data from bridge {Remote} for {Seconds:F0} s, reconnecting until it answers",
                        remote, silentMs / 1000.0);
                else
                    Logger.LogDebug("FCAM: still no data from bridge {Remote}, reconnecting", remote);
                end = SessionEnd.Silence;
                break;
            }
        }
        finally
        {
            if (remote is not null)
            {
                try
                {
                    socket.SendTo(FcamHeader.Control(FcamMessageType.Unsubscribe, ClientName, NowMs()), remote);
                }
                catch (Exception)
                {
                    // best effort: the bridge times the subscription out on its own
                }
            }

            session.Cancel();
            try { socket.Close(); } catch (Exception) { /* already closed */ }

            try
            {
                await Task.WhenAll(receive, keepalive).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the loops end by cancellation or with the socket error that ended the session
            }

            socket.Dispose();
        }

        return end;
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[FcamProtocol.MaxDatagramLength];
        var anyEndpoint = new IPEndPoint(
            socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndpoint, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                 or SocketError.NetworkReset or SocketError.MessageSize)
            {
                continue; // ICMP unreachable for an earlier send, or an oversized datagram: keep listening
            }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) break;
                Logger.LogWarning("FCAM: receive failed ({Error}), reopening the socket", ex.SocketErrorCode);
                break;
            }

            HandleDatagram(buffer.AsSpan(0, result.ReceivedBytes), result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram, EndPoint sender)
    {
        if (!FcamHeader.TryParse(datagram, out var header)) return;
        var payload = datagram[FcamProtocol.HeaderLength..];
        if (payload.Length != header.PayloadLength) return;
        _datagrams++;

        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastDatagramTicks, now);
        var lostSince = Interlocked.Exchange(ref _lostSinceTicks, 0);
        if (lostSince != 0)
            Logger.LogInformation("FCAM: bridge {Sender} answers again after {Seconds:F0} s", sender,
                (now - lostSince) / 1000.0);

        switch (header.Type)
        {
            case FcamMessageType.Frame:
                HandleFrameChunk(header, payload, sender, now);
                break;
            case FcamMessageType.Status:
                HandleStatus(payload, sender);
                break;
        }

        LogStatsIfDue();
    }

    private void HandleFrameChunk(in FcamHeader header, ReadOnlySpan<byte> payload, EndPoint sender, long now)
    {
        var resyncs = _assembler.Resyncs;
        var frame = _assembler.Add(header, payload);
        if (_assembler.Resyncs != resyncs)
            Logger.LogInformation("FCAM: {Sender} restarted its frame counter (now {Seq}), resynchronised",
                sender, header.FrameSeq);

        var sinceLastChunk = now - _lastChunkTicks;
        _lastChunkTicks = now;
        if (frame is not null)
        {
            _stallSinceTicks = 0;
            DecodeFrame(frame, sender);
            return;
        }

        // only a continuous flow of chunks without a complete frame counts as a stall, not a pause in the stream
        if (_stallSinceTicks == 0 || sinceLastChunk >= FrameStallReset.TotalMilliseconds)
        {
            _stallSinceTicks = now;
            return;
        }

        if (now - _stallSinceTicks < FrameStallReset.TotalMilliseconds) return;
        _stallSinceTicks = 0;
        _assembler.Reset();
        Logger.LogDebug("FCAM: no frame completed for {Seconds:F0} s although chunks arrive, starting over",
            FrameStallReset.TotalSeconds);
    }

    private void DecodeFrame(byte[] frame, EndPoint sender)
    {
        Mat? mat = null;
        try
        {
            mat = Mat.FromImageData(frame, ImreadModes.Color);
            if (mat.Empty() || mat.Width <= 0 || mat.Height <= 0)
            {
                mat.Dispose();
                mat = null;
            }
        }
        catch (Exception ex)
        {
            mat?.Dispose();
            mat = null;
            Logger.LogDebug(ex, "FCAM: JPEG decode failed");
        }

        if (mat is null)
        {
            Interlocked.Increment(ref _decodeFailures);
            return;
        }

        if (_framesDecoded == 0 || !Equals(sender, _lastSender))
        {
            _lastSender = sender;
            Logger.LogInformation("FCAM: receiving {Width}x{Height} frames ({Bytes} bytes) from {Sender}",
                mat.Width, mat.Height, frame.Length, sender);
        }

        Interlocked.Increment(ref _framesDecoded);
        _statsFrames++;
        SetRawMat(mat);
    }

    private void HandleStatus(ReadOnlySpan<byte> payload, EndPoint sender)
    {
        var text = Encoding.UTF8.GetString(payload);
        var state = ParseField(text, "state");
        if (state != _lastState)
        {
            _lastState = state;
            Logger.LogInformation("FCAM: bridge {Sender} is {State} ({Details})", sender,
                state.Length == 0 ? "in an unknown state" : state, text);
        }
        else
        {
            Logger.LogDebug("FCAM: status {Details}", text);
        }
    }

    private void LogStatsIfDue()
    {
        if (_statsWatch.Elapsed < StatsInterval) return;
        var seconds = _statsWatch.Elapsed.TotalSeconds;
        Logger.LogDebug(
            "FCAM: {Fps:F1} fps, {Frames} frames, {Dropped} dropped, {Rejected} rejected chunks, {Failed} undecodable, {Datagrams} datagrams, {Reconnects} reconnects",
            _statsFrames / seconds, _framesDecoded, _assembler.DroppedFrames, _assembler.RejectedChunks,
            _decodeFailures, _datagrams, Reconnects);
        _statsFrames = 0;
        _statsWatch.Restart();
    }

    private async Task KeepaliveLoopAsync(Socket socket, IPEndPoint remote, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var subscribe = FcamHeader.Control(FcamMessageType.Subscribe, ClientName, NowMs());
                await socket.SendToAsync(subscribe, SocketFlags.None, remote, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // the network may be down for a moment; keep trying, the silence check handles the rest
                Logger.LogDebug("FCAM: subscribe to {Remote} failed: {Error}", remote, ex.SocketErrorCode);
            }

            if (!await DelayAsync(SubscribeInterval, token).ConfigureAwait(false)) break;
        }
    }

    /// <summary>Waits for <paramref name="delay"/>; false when the token was cancelled meanwhile.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string ParseField(string text, string key)
    {
        foreach (var part in text.Split(';'))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part.AsSpan(0, equals).SequenceEqual(key)) return part[(equals + 1)..];
        }

        return "";
    }

    private static IPEndPoint Resolve(string host, int port)
    {
        if (IPAddress.TryParse(host, out var literal)) return new IPEndPoint(literal, port);

        var addresses = Dns.GetHostAddresses(host);
        var chosen = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault()
                     ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(chosen, port);
    }

    private static uint NowMs() => (uint)(Environment.TickCount64 & 0xFFFFFFFF);

    /// <summary>
    /// On Windows a UDP socket reports ICMP port-unreachable for an earlier send as an error on the next
    /// receive. Turn that off so a bridge that is not up yet does not break the receive loop.
    /// </summary>
    private static void DisableIcmpResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const int sioUdpConnReset = -1744830452; // SIO_UDP_CONNRESET
            socket.IOControl(sioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception)
        {
            // not fatal, the receive loop also tolerates ConnectionReset
        }
    }
}
