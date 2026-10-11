using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BestStoriesApi.Stories;

/// <summary>
/// One refresh's ranked stories, already turned into the bytes every response is made of.
///
/// The JSON for the whole list is written once, when the snapshot is built: <c>[s0,s1,...,sK]</c>. The answer
/// for any n is then the first part of that buffer, cut just after story n-1, plus a closing <c>]</c>.
/// So a request does no serialisation and no allocation of its own: it copies a slice to the socket.
/// Each answer is gzipped here too, once, for the clients that accept it.
///
/// Immutable once built, and published by swapping one reference (see <see cref="StoryStore"/>), so a reader
/// can never see a half-built snapshot and needs no lock.
/// </summary>
public sealed class StorySnapshot
{
    private const string EmptyETag = "\"empty\"";

    private static readonly byte[] Close = "]"u8.ToArray();

    private readonly byte[] _json;        // "[s0,s1,...,sK]"
    private readonly int[] _endOf;        // _endOf[i] = index in _json just past story i
    private readonly string[] _etags;     // _etags[i] = the entity tag of the answer holding stories 0..i
    private readonly byte[][] _gzipped;   // _gzipped[k] = the answer holding the first k stories, gzipped ("[]" for k = 0)
    private readonly string[] _gzipETags; // _gzipETags[k] = its entity tag

    private StorySnapshot(
        IReadOnlyList<Story> stories, DateTimeOffset builtAt, DateTimeOffset nextRefreshDue,
        byte[] json, int[] endOf, string[] etags, byte[][] gzipped, string[] gzipETags)
    {
        Stories = stories;
        BuiltAt = builtAt;
        NextRefreshDue = nextRefreshDue;
        _json = json;
        _endOf = endOf;
        _etags = etags;
        _gzipped = gzipped;
        _gzipETags = gzipETags;
    }

    public IReadOnlyList<Story> Stories { get; }

    public int Count => Stories.Count;

    /// <summary>
    /// When the refresh that built this snapshot finished. The readiness check reports the snapshot's age from it.
    /// A story kept from an earlier refresh is at most <see cref="StoryRefresher.MaxRefreshesKept"/> refreshes older.
    /// </summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>
    /// When the next refresh is due to start: the refresh loop waits one interval after a refresh finishes. No newer
    /// snapshot can be published before then, so every answer from this one is current at least until then, and the
    /// endpoint lets caches keep it that long.
    /// </summary>
    public DateTimeOffset NextRefreshDue { get; }

    /// <summary>
    /// Serialises the ranked stories once, recording where each one ends and a hash of every prefix, then gzips the
    /// answer for every n.
    /// </summary>
    public static StorySnapshot Create(IReadOnlyList<Story> stories, DateTimeOffset builtAt, DateTimeOffset nextRefreshDue)
    {
        using var buffer = new MemoryStream(stories.Count * 200 + 2);
        var endOf = new int[stories.Count];
        var etags = new string[stories.Count];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> digest = stackalloc byte[32];

        buffer.WriteByte((byte)'[');
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = StoryJsonContext.Default.Options.Encoder }))
        {
            for (var i = 0; i < stories.Count; i++)
            {
                if (i > 0) buffer.WriteByte((byte)',');
                JsonSerializer.Serialize(writer, stories[i], StoryJsonContext.Default.Story);
                writer.Flush();
                writer.Reset();
                endOf[i] = (int)buffer.Length;

                // The tag names the content of the answer for n = i + 1, so it stays the same across refreshes
                // for as long as those stories and their scores do. That is what makes a 304 worth having.
                hash.AppendData(buffer.GetBuffer(), i == 0 ? 0 : endOf[i - 1], endOf[i] - (i == 0 ? 0 : endOf[i - 1]));
                hash.GetCurrentHash(digest);
                etags[i] = $"\"{Convert.ToHexStringLower(digest[..12])}\"";
            }
        }
        buffer.WriteByte((byte)']');
        var json = buffer.ToArray();

        // Why every answer is gzipped here, once a refresh, rather than per request or not at all. Measured on
        // 11 October 2026 on one core of a 2-vCPU cloud VM, with 200 real stories (method: docs/performance.md):
        // - Bytes: n=200 goes from 44,065 to 15,408 (2.9 times smaller); n=10 from 1,959 to 833.
        // - Most requests/s for n=200: 29,300 plain, 44,100 gzipped (+51%). For n=10, no change.
        // - Gzipping per request instead (ASP.NET Core's response compression middleware): 1,500/s for n=200.
        // - The cost: about 125 ms of one core per refresh, and 1.5 MB per snapshot, both growing with the square
        //   of the number of stories, since each answer is compressed whole.
        // Remove it if something in front of the API (a CDN or gateway) compresses and caches the answers, or if
        // clients stop asking for gzip. The plain path does not depend on it: taking it out means this loop, Gzipped
        // and GzipETag below, AcceptEncoding.cs, the gzip branches in BestStoriesEndpoint, and their tests.
        // Optimal, measured on the same stories: within 1.2% of SmallestSize's output, in two thirds of the time.
        var gzipped = new byte[stories.Count + 1][];
        var gzipETags = new string[stories.Count + 1];
        using var compressed = new MemoryStream();
        for (var k = 0; k <= stories.Count; k++)
        {
            compressed.SetLength(0);
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(json, 0, k == 0 ? 1 : endOf[k - 1]);
                gzip.Write(Close);
            }
            gzipped[k] = compressed.ToArray();

            // A strong tag names one exact body, so the gzipped body gets a tag of its own, from its own bytes.
            // Compression is deterministic, so the tag still survives refreshes in which the stories did not change.
            SHA256.HashData(gzipped[k], digest);
            gzipETags[k] = $"\"{Convert.ToHexStringLower(digest[..12])}\"";
        }

        return new StorySnapshot(stories, builtAt, nextRefreshDue, json, endOf, etags, gzipped, gzipETags);
    }

    /// <summary>How many stories the answer for n holds: n, or all of them if there are fewer.</summary>
    public int Take(int n) => Math.Min(n, Count);

    /// <summary>The answer for n without its closing bracket: everything up to the end of story n-1.</summary>
    public ReadOnlyMemory<byte> Head(int n) => Take(n) == 0 ? "["u8.ToArray() : _json.AsMemory(0, _endOf[Take(n) - 1]);

    /// <summary>The closing bracket every answer ends with.</summary>
    public static ReadOnlyMemory<byte> Tail => Close;

    /// <summary>The length of the whole answer for n, for the Content-Length header.</summary>
    public int Length(int n) => Head(n).Length + 1;

    /// <summary>The entity tag of the answer for n.</summary>
    public string ETag(int n) => Take(n) == 0 ? EmptyETag : _etags[Take(n) - 1];

    /// <summary>The answer for n, gzipped.</summary>
    public ReadOnlyMemory<byte> Gzipped(int n) => _gzipped[Take(n)];

    /// <summary>The entity tag of the gzipped answer for n.</summary>
    public string GzipETag(int n) => _gzipETags[Take(n)];
}
