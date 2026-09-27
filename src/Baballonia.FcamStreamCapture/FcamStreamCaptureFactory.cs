using Baballonia.SDK;
using Microsoft.Extensions.Logging;

namespace Baballonia.FcamStreamCapture;

/// <summary>
/// Claims camera addresses of the form <c>fcam://host:port</c> (subscribe to a bridge) or
/// <c>fcam://:port</c> (listen for pushed frames). See PROTOCOL.md and README.md in this folder.
/// </summary>
public class FcamStreamCaptureFactory(ILoggerFactory loggerFactory) : ICaptureFactory
{
    public Capture Create(string address)
    {
        return new FcamStreamCapture(address, loggerFactory.CreateLogger<FcamStreamCapture>());
    }

    public bool CanConnect(string address)
    {
        return FcamAddress.TryParse(address, out _);
    }

    public string GetProviderName() => "FCAM UDP Stream";
}
