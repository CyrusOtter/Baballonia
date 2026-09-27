// FcamProbe: receive an FCAM stream with the real Baballonia capture module and print frame statistics.
//
//   dotnet run --project tools/FcamProbe -- fcam://192.0.2.10:8555 [seconds] [--save frame.jpg]
//
// Exit code 0 when at least one frame was decoded, 1 otherwise.

using System.Diagnostics;
using Baballonia.FcamStreamCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

var address = args.Length > 0 ? args[0] : "fcam://127.0.0.1:8555";
var seconds = args.Length > 1 && int.TryParse(args[1], out var parsedSeconds) ? parsedSeconds : 10;
var saveIndex = Array.IndexOf(args, "--save");
var savePath = saveIndex >= 0 && saveIndex + 1 < args.Length ? args[saveIndex + 1] : null;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss.fff ")
    .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));

var factory = new FcamStreamCaptureFactory(loggerFactory);
if (!factory.CanConnect(address))
{
    Console.Error.WriteLine($"'{address}' is not an fcam:// address");
    return 2;
}

using var capture = factory.Create(address);
if (!await capture.StartCapture())
{
    Console.Error.WriteLine("capture did not start");
    return 3;
}

long total = 0;
long windowFrames = 0;
var size = "";
var watch = Stopwatch.StartNew();
var nextReport = 1000L;
var saved = false;

while (watch.Elapsed.TotalSeconds < seconds)
{
    var mat = capture.AcquireRawMat();
    if (mat is not null)
    {
        total++;
        windowFrames++;
        size = $"{mat.Width}x{mat.Height}x{mat.Channels()}";
        if (!saved && savePath is not null)
        {
            Cv2.ImWrite(savePath, mat);
            saved = true;
            Console.WriteLine($"saved first frame to {savePath}");
        }

        mat.Dispose();
    }
    else
    {
        await Task.Delay(2);
    }

    if (watch.ElapsedMilliseconds >= nextReport)
    {
        Console.WriteLine($"{watch.Elapsed.TotalSeconds,5:F0}s  {windowFrames,3} fps  {size,-12} total {total}");
        windowFrames = 0;
        nextReport += 1000;
    }
}

await capture.StopCapture();
Console.WriteLine($"done: {total} frames in {watch.Elapsed.TotalSeconds:F1} s ({total / watch.Elapsed.TotalSeconds:F1} fps average)");
return total > 0 ? 0 : 1;
