using System.Globalization;

namespace Baballonia.FcamStreamCapture;

/// <summary>
/// A parsed <c>fcam://</c> camera address.
/// <list type="bullet">
/// <item><c>fcam://host[:port]</c>: subscribe mode. <see cref="Host"/> is the bridge to subscribe to.</item>
/// <item><c>fcam://:port</c>, <c>fcam://0.0.0.0:port</c>, <c>fcam://*:port</c>: listen-only mode, <see cref="Host"/> is null
/// and frames are accepted from any sender on <see cref="Port"/>.</item>
/// </list>
/// </summary>
public sealed record FcamAddress(string? Host, int Port)
{
    public bool IsListenOnly => Host is null;

    public static bool TryParse(string? address, out FcamAddress? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(address)) return false;

        var text = address.Trim();
        if (!text.StartsWith(FcamProtocol.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        text = text[FcamProtocol.Scheme.Length..].TrimEnd('/');
        if (text.Length == 0 || text.Contains('/') || text.Contains(' ')) return false;

        string host;
        string? portText = null;

        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0) return false;
            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':')) return false;
                portText = rest[1..];
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':');
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        else
        {
            host = text; // host name, IPv4 literal, or a bare IPv6 literal
        }

        var port = FcamProtocol.DefaultPort;
        if (portText is not null &&
            (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            return false;

        var listenOnly = host.Length == 0 || host == "*" || host == "0.0.0.0" || host == "::";
        result = new FcamAddress(listenOnly ? null : host, port);
        return true;
    }

    public override string ToString() =>
        Host is null ? $"{FcamProtocol.Scheme}:{Port}" : $"{FcamProtocol.Scheme}{Host}:{Port}";
}
