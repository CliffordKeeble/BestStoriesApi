using Microsoft.Extensions.Primitives;

namespace BestStoriesApi.Api;

/// <summary>Reads the Accept-Encoding request header (RFC 9110, section 12.5.3) without allocating.</summary>
public static class AcceptEncoding
{
    /// <summary>
    /// Whether the client accepts gzip: it lists gzip, or "*", with a weight above zero. A weight of zero refuses a
    /// coding, and a coding named outright overrides "*". No header, or an empty one, gets the plain answer.
    /// Two simplifications, both allowed by the RFC: weights are only compared with zero, so a client that accepts
    /// gzip at all gets it even if it ranks identity higher; and "x-gzip", an old alias, is not recognised.
    /// </summary>
    public static bool AllowsGzip(StringValues header)
    {
        bool? gzip = null, any = null;
        foreach (var value in header)
        {
            var rest = value.AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.IndexOf(',');
                var item = comma < 0 ? rest : rest[..comma];
                rest = comma < 0 ? [] : rest[(comma + 1)..];

                var semicolon = item.IndexOf(';');
                var coding = (semicolon < 0 ? item : item[..semicolon]).Trim();
                var accepted = semicolon < 0 || !IsWeightZero(item[(semicolon + 1)..]);
                if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                    gzip = accepted;
                else if (coding is "*")
                    any = accepted;
            }
        }
        return gzip ?? any ?? false;
    }

    /// <summary>q=0, with or without zero decimals. Any other weight, even a malformed one, accepts the coding.</summary>
    private static bool IsWeightZero(ReadOnlySpan<char> parameters)
    {
        var weight = parameters.Trim();
        if (weight.Length < 3 || weight[0] is not ('q' or 'Q') || weight[1] != '=' || weight[2] != '0')
            return false;
        var decimals = weight[3..];
        return decimals.IsEmpty || (decimals[0] == '.' && decimals[1..].TrimEnd('0').IsEmpty);
    }
}
