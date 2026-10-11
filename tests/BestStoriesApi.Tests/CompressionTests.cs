using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using BestStoriesApi.Api;
using Microsoft.Extensions.Primitives;

namespace BestStoriesApi.Tests;

/// <summary>Gzip: built once a refresh, sent to the clients that accept it, under a tag of its own.</summary>
public class CompressionTests
{
    private const string Path = "/api/beststories?n=2";

    private static BestStoriesApiFactory App() => new(TestData.FakeHackerNews(
        (1, TestData.Item("one", 30)), (2, TestData.Item("two", 20)), (3, TestData.Item("three", 10))));

    private static HttpRequestMessage Request(HttpMethod method, string? acceptEncoding, EntityTagHeaderValue? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, Path);
        if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        if (ifNoneMatch is not null) request.Headers.IfNoneMatch.Add(ifNoneMatch);
        return request;
    }

    private static byte[] Gunzip(byte[] gzipped)
    {
        using var unzipped = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        unzipped.CopyTo(plain);
        return plain.ToArray();
    }

    [Test]
    public async Task A_client_that_accepts_gzip_gets_the_same_answer_gzipped_with_a_tag_of_its_own()
    {
        await using var app = App();
        using var http = app.CreateClient();

        var plain = await http.SendAsync(Request(HttpMethod.Get, null));
        var zipped = await http.SendAsync(Request(HttpMethod.Get, "gzip, deflate, br"));
        var zippedBody = await zipped.Content.ReadAsByteArrayAsync();

        Assert.That(zipped.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(zipped.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
        Assert.That(zipped.Content.Headers.ContentLength, Is.EqualTo(zippedBody.Length));
        Assert.That(Gunzip(zippedBody), Is.EqualTo(await plain.Content.ReadAsByteArrayAsync()));
        Assert.That(zipped.Headers.ETag, Is.Not.EqualTo(plain.Headers.ETag));
        Assert.That(plain.Content.Headers.ContentEncoding, Is.Empty);
        // Both depend on Accept-Encoding, so both say so: a cache must keep one copy per encoding.
        Assert.That(plain.Headers.Vary, Does.Contain("Accept-Encoding"));
        Assert.That(zipped.Headers.Vary, Does.Contain("Accept-Encoding"));
    }

    [Test]
    public async Task Each_encoding_has_its_own_304()
    {
        await using var app = App();
        using var http = app.CreateClient();
        var plainTag = (await http.SendAsync(Request(HttpMethod.Get, null))).Headers.ETag!;
        var gzipTag = (await http.SendAsync(Request(HttpMethod.Get, "gzip"))).Headers.ETag!;

        var gzipWithItsTag = await http.SendAsync(Request(HttpMethod.Get, "gzip", gzipTag));
        var gzipWithThePlainTag = await http.SendAsync(Request(HttpMethod.Get, "gzip", plainTag));
        var plainWithTheGzipTag = await http.SendAsync(Request(HttpMethod.Get, null, gzipTag));

        Assert.That(gzipWithItsTag.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(gzipWithItsTag.Headers.ETag, Is.EqualTo(gzipTag));
        Assert.That(gzipWithItsTag.Headers.Vary, Does.Contain("Accept-Encoding"));
        Assert.That(gzipWithThePlainTag.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(gzipWithThePlainTag.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
        Assert.That(plainWithTheGzipTag.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(plainWithTheGzipTag.Content.Headers.ContentEncoding, Is.Empty);
    }

    [Test]
    public async Task HEAD_with_gzip_gets_the_gzip_headers_and_no_body()
    {
        await using var app = App();
        using var http = app.CreateClient();
        var get = await http.SendAsync(Request(HttpMethod.Get, "gzip"));

        var head = await http.SendAsync(Request(HttpMethod.Head, "gzip"));

        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(head.Headers.ETag, Is.EqualTo(get.Headers.ETag));
        Assert.That(head.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
        Assert.That(head.Content.Headers.ContentLength, Is.EqualTo((await get.Content.ReadAsByteArrayAsync()).Length));
        Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
    }

    [TestCase("gzip", true)]
    [TestCase("GZip", true)]
    [TestCase("deflate, gzip;q=1.0, br", true)]
    [TestCase("gzip ; q=0.5", true)]
    [TestCase("gzip;q=0.001", true)]
    [TestCase("*", true)]
    [TestCase("br, *;q=0.1", true)]
    [TestCase("*;q=0, gzip", true)]
    [TestCase("gzip;q=0", false)]
    [TestCase("gzip; Q=0.000", false)]
    [TestCase("*;q=0", false)]
    [TestCase("gzip;q=0, *", false)]
    [TestCase("*, gzip;q=0", false)]
    [TestCase("br, deflate", false)]
    [TestCase("identity", false)]
    [TestCase("", false)]
    // Malformed headers must not throw. A weight that is not a clear zero accepts the coding.
    [TestCase("gzip;", true)]
    [TestCase("gzip;q=", true)]
    [TestCase("gzip;q=0.", false)]
    [TestCase(";", false)]
    [TestCase(",,", false)]
    [TestCase("q=0.", false)]
    public void Accept_Encoding_decides_whether_the_answer_is_gzipped(string header, bool gzipped) =>
        Assert.That(AcceptEncoding.AllowsGzip(header), Is.EqualTo(gzipped));

    [Test]
    public void No_Accept_Encoding_gets_the_plain_answer_and_several_header_lines_are_read_together()
    {
        Assert.That(AcceptEncoding.AllowsGzip(StringValues.Empty), Is.False);
        Assert.That(AcceptEncoding.AllowsGzip(new StringValues(["br", "gzip;q=0.8"])), Is.True);
        Assert.That(AcceptEncoding.AllowsGzip(new StringValues([null, "gzip"])), Is.True);
    }
}
