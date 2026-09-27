using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using Baballonia.FcamStreamCapture;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using Capture = Baballonia.SDK.Capture;
using FcamCapture = Baballonia.FcamStreamCapture.FcamStreamCapture;

namespace Baballonia.Tests;

[TestClass]
public class FcamProtocolTests
{
    [TestMethod]
    public void HeaderRoundTrips()
    {
        var header = new FcamHeader(FcamMessageType.Frame, FcamProtocol.CodecJpeg, 0, 65535, 3, 7, 1400, 123456, 4200,
            0xDEADBEEF);
        var bytes = header.ToArray();

        Assert.AreEqual(FcamProtocol.HeaderLength, bytes.Length);
        Assert.IsTrue(FcamHeader.TryParse(bytes, out var parsed));
        Assert.AreEqual(header, parsed);
    }

    [TestMethod]
    public void HeaderIsBigEndianWithMagic()
    {
        var bytes = new FcamHeader(FcamMessageType.Status, 0, FcamProtocol.FlagSourcePresent, 0x0102, 0, 0, 5, 0, 0, 0)
            .ToArray();

        CollectionAssert.AreEqual("FCAM"u8.ToArray(), bytes[..4]);
        Assert.AreEqual(FcamProtocol.Version, bytes[4]);
        Assert.AreEqual((byte)FcamMessageType.Status, bytes[5]);
        Assert.AreEqual(0x01, bytes[8]);
        Assert.AreEqual(0x02, bytes[9]);
        Assert.AreEqual(5, bytes[15]);
    }

    [TestMethod]
    public void HeaderRejectsGarbage()
    {
        var good = new FcamHeader(FcamMessageType.Frame, 1, 0, 1, 0, 1, 10, 10, 0, 0).ToArray();

        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.IsFalse(FcamHeader.TryParse(badMagic, out _));

        var badVersion = (byte[])good.Clone();
        badVersion[4] = 2;
        Assert.IsFalse(FcamHeader.TryParse(badVersion, out _));

        var badType = (byte[])good.Clone();
        badType[5] = 9;
        Assert.IsFalse(FcamHeader.TryParse(badType, out _));

        Assert.IsFalse(FcamHeader.TryParse(good[..20], out _));
    }

    [TestMethod]
    public void ChunkFrameCoversTheWholeFrame()
    {
        var frame = Enumerable.Range(0, 3001).Select(i => (byte)i).ToArray();
        var chunks = FcamHeader.ChunkFrame(frame, 5, 1000).ToList();

        Assert.AreEqual(4, chunks.Count);
        var assembler = new FcamFrameAssembler();
        byte[]? result = null;
        foreach (var chunk in chunks)
        {
            Assert.IsTrue(FcamHeader.TryParse(chunk, out var header));
            Assert.AreEqual((ushort)5, header.FrameSeq);
            Assert.AreEqual((ushort)4, header.ChunkCount);
            result = assembler.Add(header, chunk.AsSpan(FcamProtocol.HeaderLength));
        }

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(frame, result);
    }

    [TestMethod]
    [DataRow("fcam://192.0.2.10:8555", "192.0.2.10", 8555)]
    [DataRow("fcam://192.0.2.10", "192.0.2.10", FcamProtocol.DefaultPort)]
    [DataRow("FCAM://headset.local:9000/", "headset.local", 9000)]
    [DataRow("fcam://[fd00::1]:8555", "fd00::1", 8555)]
    [DataRow("fcam://fd00::1", "fd00::1", FcamProtocol.DefaultPort)]
    [DataRow("fcam://:8555", null, 8555)]
    [DataRow("fcam://0.0.0.0:8556", null, 8556)]
    [DataRow("fcam://*:8557", null, 8557)]
    public void AddressParses(string address, string? host, int port)
    {
        Assert.IsTrue(FcamAddress.TryParse(address, out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(host, parsed.Host);
        Assert.AreEqual(port, parsed.Port);
        Assert.AreEqual(host is null, parsed.IsListenOnly);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("fcam://")]
    [DataRow("fcam:///")]
    [DataRow("http://192.0.2.10:8555")]
    [DataRow("COM6")]
    [DataRow("/dev/ttyACM0")]
    [DataRow("fcam://host:")]
    [DataRow("fcam://host:0")]
    [DataRow("fcam://host:70000")]
    [DataRow("fcam://host:abc")]
    [DataRow("fcam://host:8555/stream")]
    [DataRow("fcam://[fd00::1")]
    public void AddressRejects(string address)
    {
        Assert.IsFalse(FcamAddress.TryParse(address, out _));
    }
}

[TestClass]
public class FcamFrameAssemblerTests
{
    private static byte[] Frame(int length, byte seed) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    private static byte[]? Feed(FcamFrameAssembler assembler, byte[] datagram)
    {
        Assert.IsTrue(FcamHeader.TryParse(datagram, out var header));
        return assembler.Add(header, datagram.AsSpan(FcamProtocol.HeaderLength));
    }

    [TestMethod]
    public void AssemblesOutOfOrderChunks()
    {
        var frame = Frame(2500, 1);
        var assembler = new FcamFrameAssembler();
        var chunks = FcamHeader.ChunkFrame(frame, 1, 700).ToList();
        chunks.Reverse();

        byte[]? result = null;
        foreach (var chunk in chunks) result = Feed(assembler, chunk);

        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(frame, result);
        Assert.AreEqual(1, assembler.CompletedFrames);
        Assert.AreEqual(0, assembler.DroppedFrames);
    }

    [TestMethod]
    public void DropsIncompleteFrameWhenNextOneStarts()
    {
        var assembler = new FcamFrameAssembler();
        var first = FcamHeader.ChunkFrame(Frame(2000, 2), 10, 500).ToList();
        var second = FcamHeader.ChunkFrame(Frame(900, 3), 11, 500).ToList();

        Assert.IsNull(Feed(assembler, first[0]));
        Assert.IsNull(Feed(assembler, first[2])); // chunk 1 of the first frame is lost
        Assert.IsNull(Feed(assembler, second[0]));
        Assert.IsNotNull(Feed(assembler, second[1]));

        Assert.AreEqual(1, assembler.DroppedFrames);
        Assert.AreEqual(1, assembler.CompletedFrames);
    }

    [TestMethod]
    public void IgnoresDuplicatesAndStragglers()
    {
        var assembler = new FcamFrameAssembler();
        var old = FcamHeader.ChunkFrame(Frame(600, 4), 20, 500).ToList();
        var current = FcamHeader.ChunkFrame(Frame(600, 5), 21, 500).ToList();

        Assert.IsNull(Feed(assembler, current[0]));
        Assert.IsNull(Feed(assembler, current[0])); // duplicate
        Assert.IsNull(Feed(assembler, old[1])); // straggler from the previous frame
        Assert.IsNotNull(Feed(assembler, current[1]));
        Assert.IsNull(Feed(assembler, current[1])); // late duplicate after completion

        Assert.AreEqual(1, assembler.CompletedFrames);
        Assert.AreEqual(0, assembler.DroppedFrames);
        Assert.AreEqual(1, assembler.RejectedChunks);
    }

    [TestMethod]
    public void RejectsChunksThatOverflowTheFrame()
    {
        var assembler = new FcamFrameAssembler();
        var header = new FcamHeader(FcamMessageType.Frame, 1, 0, 1, 0, 1, 100, 50, 0, 0);
        Assert.IsNull(assembler.Add(header, new byte[100]));
        Assert.AreEqual(1, assembler.RejectedChunks);

        var oversized = new FcamHeader(FcamMessageType.Frame, 1, 0, 2, 0, 1, 1, (uint)FcamProtocol.MaxFrameLength + 1,
            0, 0);
        Assert.IsNull(assembler.Add(oversized, new byte[1]));
        Assert.AreEqual(2, assembler.RejectedChunks);
    }

    [TestMethod]
    public void ResyncsWhenTheSenderRestartsItsCounter()
    {
        var assembler = new FcamFrameAssembler();
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 6), 25863, 500).Single()));

        // a restarted bridge counts from 0 again, which is 25863 frames "behind" on the 16-bit ring
        var restarted = FcamHeader.ChunkFrame(Frame(1200, 7), 0, 500).ToList();
        Assert.IsNull(Feed(assembler, restarted[0]));
        Assert.IsNull(Feed(assembler, restarted[1]));
        Assert.IsNotNull(Feed(assembler, restarted[2]));
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 8), 1, 500).Single()));

        Assert.AreEqual(3, assembler.CompletedFrames);
        Assert.AreEqual(1, assembler.Resyncs);
    }

    [TestMethod]
    public void ResyncsWhileAFrameIsInProgress()
    {
        var assembler = new FcamFrameAssembler();
        var interrupted = FcamHeader.ChunkFrame(Frame(1000, 9), 30000, 500).ToList();
        Assert.IsNull(Feed(assembler, interrupted[0])); // the old bridge stopped in the middle of a frame

        var restarted = FcamHeader.ChunkFrame(Frame(1000, 10), 5, 500).ToList();
        Assert.IsNull(Feed(assembler, restarted[0]));
        Assert.IsNotNull(Feed(assembler, restarted[1]));

        Assert.AreEqual(1, assembler.DroppedFrames);
        Assert.AreEqual(1, assembler.Resyncs);
    }

    [TestMethod]
    public void OnlyFramesWithinTheReorderWindowCountAsStragglers()
    {
        var assembler = new FcamFrameAssembler();
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 11), 100, 500).Single()));

        var straggler = (ushort)(100 - FcamFrameAssembler.ReorderWindow);
        Assert.IsNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 12), straggler, 500).Single()));
        Assert.AreEqual(0, assembler.Resyncs);

        var restart = (ushort)(100 - FcamFrameAssembler.ReorderWindow - 1);
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 13), restart, 500).Single()));
        Assert.AreEqual(1, assembler.Resyncs);
    }

    [TestMethod]
    public void ResetForgetsTheSequence()
    {
        var assembler = new FcamFrameAssembler();
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 14), 500, 500).Single()));
        Assert.IsNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 15), 499, 500).Single()));

        assembler.Reset();
        Assert.IsNotNull(Feed(assembler, FcamHeader.ChunkFrame(Frame(300, 15), 499, 500).Single()));
        Assert.AreEqual(0, assembler.Resyncs);
    }
}

[TestClass]
public class FcamStreamCaptureTests
{
    private static byte[] MakeJpeg(out int width, out int height)
    {
        width = 64;
        height = 48;
        using var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Cv2.ImEncode(".jpg", mat, out var bytes);
        return bytes;
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task<Mat?> WaitForFrame(Capture capture, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var mat = capture.AcquireRawMat();
            if (mat is not null) return mat;
            await Task.Delay(5);
        }

        return null;
    }

    [TestMethod]
    public void FactoryClaimsOnlyFcamAddresses()
    {
        var factory = new FcamStreamCaptureFactory(new LoggerFactory());
        Assert.IsTrue(factory.CanConnect("fcam://192.0.2.10:8555"));
        Assert.IsTrue(factory.CanConnect("fcam://:8555"));
        Assert.IsFalse(factory.CanConnect("http://192.0.2.10:8555"));
        Assert.IsFalse(factory.CanConnect("COM6"));
        Assert.IsFalse(factory.CanConnect("OBS Virtual Camera"));
        Assert.IsFalse(factory.CanConnect("0"));
    }

    [TestMethod]
    public async Task ListenModeReceivesChunkedJpeg()
    {
        var port = FreeUdpPort();
        var factory = new FcamStreamCaptureFactory(new LoggerFactory());
        using var capture = factory.Create($"fcam://:{port}");
        Assert.IsTrue(await capture.StartCapture());
        Assert.IsTrue(capture.IsReady);

        var jpeg = MakeJpeg(out var width, out var height);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        foreach (var datagram in FcamHeader.ChunkFrame(jpeg, 1, 500))
            await sender.SendAsync(datagram, datagram.Length, target);

        using var mat = await WaitForFrame(capture, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(mat);
        Assert.AreEqual(width, mat.Width);
        Assert.AreEqual(height, mat.Height);
        Assert.AreEqual(3, mat.Channels());

        Assert.IsTrue(await capture.StopCapture());
        Assert.IsFalse(capture.IsReady);
    }

    [TestMethod]
    public async Task SubscribeModeSendsKeepalivesAndReceivesReplies()
    {
        using var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bridgePort = ((IPEndPoint)bridge.Client.LocalEndPoint!).Port;
        var factory = new FcamStreamCaptureFactory(new LoggerFactory());
        using var capture = factory.Create($"fcam://127.0.0.1:{bridgePort}");
        Assert.IsTrue(await capture.StartCapture());

        using var subscribeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await bridge.ReceiveAsync(subscribeTimeout.Token);
        Assert.IsTrue(FcamHeader.TryParse(received.Buffer, out var header));
        Assert.AreEqual(FcamMessageType.Subscribe, header.Type);
        Assert.AreEqual("Baballonia", System.Text.Encoding.UTF8.GetString(received.Buffer[FcamProtocol.HeaderLength..]));

        var status = FcamHeader.Control(FcamMessageType.Status, "state=streaming;source=test"u8, 0,
            FcamProtocol.FlagSourcePresent);
        await bridge.SendAsync(status, status.Length, received.RemoteEndPoint);

        var jpeg = MakeJpeg(out var width, out var height);
        foreach (var datagram in FcamHeader.ChunkFrame(jpeg, 7, 700).Reverse())
            await bridge.SendAsync(datagram, datagram.Length, received.RemoteEndPoint);

        using var mat = await WaitForFrame(capture, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(mat);
        Assert.AreEqual(width, mat.Width);
        Assert.AreEqual(height, mat.Height);

        await capture.StopCapture();

        var sawUnsubscribe = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!sawUnsubscribe && DateTime.UtcNow < deadline)
        {
            using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                var datagram = await bridge.ReceiveAsync(receiveTimeout.Token);
                if (FcamHeader.TryParse(datagram.Buffer, out var control) &&
                    control.Type == FcamMessageType.Unsubscribe)
                    sawUnsubscribe = true;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.IsTrue(sawUnsubscribe);
    }

    [TestMethod]
    public async Task CaptureCanBeRestarted()
    {
        var port = FreeUdpPort();
        var factory = new FcamStreamCaptureFactory(new LoggerFactory());
        using var capture = factory.Create($"fcam://:{port}");

        Assert.IsTrue(await capture.StartCapture());
        Assert.IsTrue(await capture.StopCapture());
        Assert.IsTrue(await capture.StartCapture());

        var jpeg = MakeJpeg(out var width, out _);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        foreach (var datagram in FcamHeader.ChunkFrame(jpeg, 3))
            await sender.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));

        using var mat = await WaitForFrame(capture, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(mat);
        Assert.AreEqual(width, mat.Width);
        Assert.IsTrue(await capture.StopCapture());
    }

    private static FcamCapture CreateCapture(string address, TimeSpan? reconnectAfterSilence = null,
        TimeSpan? frameStallReset = null)
    {
        var logger = new LoggerFactory().CreateLogger<FcamCapture>();
        return new FcamCapture(address, logger)
        {
            ReconnectAfterSilence = reconnectAfterSilence ?? TimeSpan.FromSeconds(5),
            FrameStallReset = frameStallReset ?? TimeSpan.FromSeconds(2),
        };
    }

    private static async Task SendFrame(UdpClient sender, byte[] jpeg, ushort seq, IPEndPoint target)
    {
        foreach (var datagram in FcamHeader.ChunkFrame(jpeg, seq, 700))
            await sender.SendAsync(datagram, datagram.Length, target);
    }

    /// <summary>Returns the sender of the next control datagram of the given type.</summary>
    private static async Task<IPEndPoint> ReceiveControl(UdpClient bridge, FcamMessageType type, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var received = await bridge.ReceiveAsync(cts.Token);
            if (FcamHeader.TryParse(received.Buffer, out var header) && header.Type == type)
                return received.RemoteEndPoint;
        }
    }

    [TestMethod]
    public async Task SubscribeModeRecoversWhenTheBridgeRestartsItsFrameCounter()
    {
        using var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bridgePort = ((IPEndPoint)bridge.Client.LocalEndPoint!).Port;
        using var capture = CreateCapture($"fcam://127.0.0.1:{bridgePort}");
        Assert.IsTrue(await capture.StartCapture());

        var subscriber = await ReceiveControl(bridge, FcamMessageType.Subscribe, TimeSpan.FromSeconds(5));
        var jpeg = MakeJpeg(out var width, out _);
        await SendFrame(bridge, jpeg, 25863, subscriber);
        using (var before = await WaitForFrame(capture, TimeSpan.FromSeconds(5)))
            Assert.IsNotNull(before);

        // the bridge restarted and counts from 0 again; the picture must come back right away,
        // well before the frame stall guard would step in
        for (ushort seq = 0; seq < 3; seq++)
            await SendFrame(bridge, jpeg, seq, subscriber);
        using var after = await WaitForFrame(capture, TimeSpan.FromSeconds(1));
        Assert.IsNotNull(after);
        Assert.AreEqual(width, after.Width);
        Assert.IsTrue(capture.FramesDecoded >= 2);

        await capture.StopCapture();
    }

    [TestMethod]
    public async Task SubscribeModeResubscribesFromANewSocketWhenTheBridgeIsSilent()
    {
        using var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var bridgePort = ((IPEndPoint)bridge.Client.LocalEndPoint!).Port;
        using var capture = CreateCapture($"fcam://127.0.0.1:{bridgePort}",
            reconnectAfterSilence: TimeSpan.FromMilliseconds(800));
        Assert.IsTrue(await capture.StartCapture());

        var first = await ReceiveControl(bridge, FcamMessageType.Subscribe, TimeSpan.FromSeconds(5));

        // the bridge stays silent, so the capture gives up on that socket and subscribes from a new one
        IPEndPoint? second = null;
        var watch = Stopwatch.StartNew();
        while (second is null && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var next = await ReceiveControl(bridge, FcamMessageType.Subscribe, TimeSpan.FromSeconds(5));
            if (!next.Equals(first)) second = next;
        }

        Assert.IsNotNull(second);
        Assert.IsTrue(capture.Reconnects >= 1);
        Assert.IsTrue(capture.IsReady);

        var jpeg = MakeJpeg(out var width, out _);
        await SendFrame(bridge, jpeg, 1, second);
        using var mat = await WaitForFrame(capture, TimeSpan.FromSeconds(5));
        Assert.IsNotNull(mat);
        Assert.AreEqual(width, mat.Width);

        await capture.StopCapture();
    }

    [TestMethod]
    public async Task StartSucceedsWhileTheHostDoesNotResolve()
    {
        using var capture = CreateCapture("fcam://fcam-test-host.invalid:8555",
            reconnectAfterSilence: TimeSpan.FromMilliseconds(200));

        Assert.IsTrue(await capture.StartCapture());
        Assert.IsTrue(capture.IsReady);
        await Task.Delay(500);
        Assert.IsTrue(await capture.StopCapture());
        Assert.IsFalse(capture.IsReady);
    }

    [TestMethod]
    public async Task FrameStallResetRecoversFromAConfusedSequence()
    {
        var port = FreeUdpPort();
        using var capture = CreateCapture($"fcam://:{port}", frameStallReset: TimeSpan.FromMilliseconds(300));
        Assert.IsTrue(await capture.StartCapture());

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var jpeg = MakeJpeg(out _, out _);
        await SendFrame(sender, jpeg, 100, target);
        using (var first = await WaitForFrame(capture, TimeSpan.FromSeconds(5)))
            Assert.IsNotNull(first);

        // frames a few numbers behind look like late stragglers; once they keep coming, the capture starts over
        var watch = Stopwatch.StartNew();
        Mat? recovered = null;
        while (recovered is null && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await SendFrame(sender, jpeg, 90, target);
            await Task.Delay(20);
            recovered = capture.AcquireRawMat();
        }

        using (recovered)
            Assert.IsNotNull(recovered);
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromMilliseconds(250));

        await capture.StopCapture();
    }
}
