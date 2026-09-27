using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Capture = Baballonia.SDK.Capture;

namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Receives JPEG frames over the FCAM/UDP protocol (PROTOCOL.md in this folder), for example from
/// a small bridge on a Steam Frame that forwards a Babble tracker plugged into its USB-C port.
/// <para>
/// With <c>fcam://host:port</c> the capture subscribes to the bridge once a second and receives the
/// frames as replies on an ephemeral port, which passes stateful firewalls without rules.
/// With <c>fcam://:port</c> it only listens on that port for frames pushed by a bridge.
/// </para>
/// </summary>
public sealed class FcamStreamCapture : Capture
{
    private static readonly TimeSpan SubscribeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);
    private static readonly byte[] ClientName = "Baballonia"u8.ToArray();

    private readonly FcamAddress _address;
    private readonly FcamFrameAssembler _assembler = new();
    private readonly object _lifecycleLock = new();
    private readonly Stopwatch _statsWatch = new();

    private Socket? _socket;
    private IPEndPoint? _remote;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;

    private long _datagrams;
    private long _framesDecoded;
    private long _decodeFailures;
    private long _statsFrames;
    private string _lastState = "";
    private EndPoint? _lastSender;

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

    public override Task<bool> StartCapture()
    {
        lock (_lifecycleLock)
        {
            if (_cts is not null) return Task.FromResult(IsReady);

            try
            {
                _remote = _address.IsListenOnly ? null : Resolve(_address.Host!, _address.Port);
                var family = _remote?.AddressFamily ?? AddressFamily.InterNetwork;
                var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                if (family == AddressFamily.InterNetworkV6) socket.DualMode = true;
                socket.ReceiveBufferSize = 4 * 1024 * 1024;
                DisableIcmpResets(socket);
                var bindAddress = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                socket.Bind(new IPEndPoint(bindAddress, _address.IsListenOnly ? _address.Port : 0));

                _socket = socket;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _statsWatch.Restart();
                _statsFrames = 0;
                _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, token), CancellationToken.None);
                if (_remote is not null)
                {
                    var remote = _remote;
                    _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(socket, remote, token), CancellationToken.None);
                }

                IsReady = true;
                var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                if (_remote is null)
                    Logger.LogInformation("FCAM: listening for frames on UDP port {Port} ({Source})", localPort, Source);
                else
                    Logger.LogInformation("FCAM: subscribing to bridge {Remote} from local UDP port {Port} ({Source})",
                        _remote, localPort, Source);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "FCAM: failed to start capture for '{Source}'", Source);
                CleanupLocked();
                IsReady = false;
            }

            return Task.FromResult(IsReady);
        }
    }

    public override Task<bool> StopCapture()
    {
        lock (_lifecycleLock)
        {
            IsReady = false;
            if (_cts is null) return Task.FromResult(true);

            Logger.LogDebug("FCAM: stopping ({Frames} frames decoded, {Dropped} dropped, {Failed} undecodable)",
                _framesDecoded, _assembler.DroppedFrames, _decodeFailures);
            CleanupLocked();
            return Task.FromResult(true);
        }
    }

    public override void Dispose()
    {
        StopCapture();
        GC.SuppressFinalize(this);
    }

    private void CleanupLocked()
    {
        var cts = _cts;
        var socket = _socket;
        var remote = _remote;
        _cts = null;
        _socket = null;

        if (socket is not null && remote is not null)
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

        cts?.Cancel();
        try { socket?.Close(); } catch (Exception) { /* already closed */ }

        try
        {
            var tasks = new[] { _receiveTask, _keepaliveTask }.OfType<Task>().ToArray();
            if (tasks.Length > 0) Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // the loops end by cancellation; their exceptions are expected here
        }

        _receiveTask = null;
        _keepaliveTask = null;
        socket?.Dispose();
        cts?.Dispose();
        _assembler.Reset();
        _lastState = "";
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
                Logger.LogWarning("FCAM: receive failed ({Error}), capture stopped", ex.SocketErrorCode);
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

        switch (header.Type)
        {
            case FcamMessageType.Frame:
                var frame = _assembler.Add(header, payload);
                if (frame is not null) DecodeFrame(frame, sender);
                break;
            case FcamMessageType.Status:
                HandleStatus(payload, sender);
                break;
        }

        LogStatsIfDue();
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
            "FCAM: {Fps:F1} fps, {Frames} frames, {Dropped} dropped, {Rejected} rejected chunks, {Failed} undecodable, {Datagrams} datagrams",
            _statsFrames / seconds, _framesDecoded, _assembler.DroppedFrames, _assembler.RejectedChunks,
            _decodeFailures, _datagrams);
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
                Logger.LogDebug("FCAM: subscribe to {Remote} failed: {Error}", remote, ex.SocketErrorCode);
            }

            try
            {
                await Task.Delay(SubscribeInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
