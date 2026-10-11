using System.Security.Cryptography;
using System.Text.Json;

namespace BestStoriesApi.Stories;

/// <summary>
/// One refresh's ranked stories, already turned into the bytes every response is made of.
///
/// The JSON for the whole list is written once, when the snapshot is built: <c>[s0,s1,...,sK]</c>. The answer
/// for any n is then the first part of that buffer, cut just after story n-1, plus a closing <c>]</c>.
/// So a request does no serialisation and no allocation of its own: it copies a slice to the socket.
///
/// Immutable once built, and published by swapping one reference (see <see cref="StoryStore"/>), so a reader
/// can never see a half-built snapshot and needs no lock.
/// </summary>
public sealed class StorySnapshot
{
    private static readonly byte[] Close = "]"u8.ToArray();

    private readonly byte[] _json;      // "[s0,s1,...,sK]"
    private readonly int[] _endOf;      // _endOf[i] = index in _json just past story i
    private readonly string[] _etags;   // _etags[i] = the entity tag of the answer holding stories 0..i

    private StorySnapshot(IReadOnlyList<Story> stories, DateTimeOffset builtAt, byte[] json, int[] endOf, string[] etags)
    {
        Stories = stories;
        BuiltAt = builtAt;
        _json = json;
        _endOf = endOf;
        _etags = etags;
    }

    public IReadOnlyList<Story> Stories { get; }

    public int Count => Stories.Count;

    /// <summary>
    /// When the refresh that built this snapshot finished. The readiness check reports the snapshot's age from it.
    /// A story kept from an earlier refresh is at most <see cref="StoryRefresher.MaxRefreshesKept"/> refreshes older.
    /// </summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>Serialises the ranked stories once, recording where each one ends and a hash of every prefix.</summary>
    public static StorySnapshot Create(IReadOnlyList<Story> stories, DateTimeOffset builtAt)
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
        return new StorySnapshot(stories, builtAt, buffer.ToArray(), endOf, etags);
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
    public string ETag(int n) => Take(n) == 0 ? "\"empty\"" : _etags[Take(n) - 1];
}
