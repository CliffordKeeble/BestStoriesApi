using System.Buffers;
using System.Globalization;
using System.Numerics;
using BestStoriesApi.HackerNews;
using BestStoriesApi.Stories;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace BestStoriesApi.Api;

/// <summary>GET /api/beststories?n=10 : the n highest-scoring best stories, highest first.</summary>
public static class BestStoriesEndpoint
{
    public const string Route = "/api/beststories";

    // The Cache-Control value for the current max-age, shared until max-age changes, so it is built about once a
    // second rather than once a request (Kestrel does the same for its Date header). Requests may race to replace
    // it; each builds the right value for its own max-age, so whichever is kept is right.
    private static volatile CacheControlHeader s_cacheControl = new(-1, "");

    public static IEndpointConventionBuilder MapBestStories(this IEndpointRouteBuilder app) =>
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], HandleAsync)
            .WithName("GetBestStories")
            .WithSummary("The n highest-scoring of Hacker News's best stories, highest first.")
            .Produces<IEnumerable<Story>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static Task HandleAsync(
        HttpContext context, StoryStore store, IOptions<HackerNewsOptions> options, TimeProvider clock, ILoggerFactory loggers)
    {
        if (!TryReadN(context.Request.Query["n"], out var n))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Query parameter 'n' is required and must be a whole number of at least 1.").ExecuteAsync(context);
        }

        // The usual case: a snapshot is already there, so answer without waiting for anything.
        var snapshot = store.Current;
        return snapshot is not null
            ? WriteAsync(context, snapshot, n, clock)
            : WaitForFirstSnapshotAsync(context, store, options.Value, clock, loggers, n);
    }

    /// <summary>
    /// Only before the first refresh has finished: wait for that one refresh, never start another, and not for
    /// longer than FirstSnapshotWait.
    /// </summary>
    private static async Task WaitForFirstSnapshotAsync(
        HttpContext context, StoryStore store, HackerNewsOptions settings, TimeProvider clock, ILoggerFactory loggers, int n)
    {
        StorySnapshot? snapshot;
        var timedOut = false;
        try
        {
            snapshot = await store.GetAsync(context.RequestAborted).WaitAsync(settings.FirstSnapshotWait, context.RequestAborted);
        }
        catch (TimeoutException)
        {
            snapshot = null;
            timedOut = true;
        }

        if (snapshot is null)
        {
            loggers.CreateLogger(typeof(BestStoriesEndpoint)).LogNoSnapshotYet(timedOut);

            // A failed first refresh is tried again after FirstSnapshotRetryInterval, and then less often, so that is the
            // soonest it is worth coming back. Retry-After is in whole seconds, and at least one.
            var retryAfter = Math.Max(1, (int)Math.Ceiling(settings.FirstSnapshotRetryInterval.TotalSeconds));
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Stories are not available yet. Try again shortly.").ExecuteAsync(context);
            return;
        }
        await WriteAsync(context, snapshot, n, clock);
    }

    /// <summary>
    /// Copies the pre-built answer to the response, gzipped if the client accepts that. No serialisation and no
    /// compression happen here.
    /// </summary>
    private static async Task WriteAsync(HttpContext context, StorySnapshot snapshot, int n, TimeProvider clock)
    {
        var response = context.Response;
        // Gzip is built with the snapshot. Why, and when to take it out: the comment in StorySnapshot.Create.
        var gzip = AcceptEncoding.AllowsGzip(context.Request.Headers.AcceptEncoding);
        var etag = gzip ? snapshot.GzipETag(n) : snapshot.ETag(n);
        response.Headers.ETag = etag;
        response.Headers.CacheControl = CacheControl(snapshot, clock);
        response.Headers.Vary = "Accept-Encoding"; // the body depends on it, so a cache must keep one copy per encoding

        if (Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.ContentType = "application/json; charset=utf-8";
        if (gzip)
            response.Headers.ContentEncoding = "gzip";
        response.ContentLength = gzip ? snapshot.Gzipped(n).Length : snapshot.Length(n);
        if (HttpMethods.IsHead(context.Request.Method))
            return; // HEAD: the same headers as GET, and no body

        var body = response.BodyWriter;
        if (gzip)
        {
            body.Write(snapshot.Gzipped(n).Span);
        }
        else
        {
            body.Write(snapshot.Head(n).Span);
            body.Write(StorySnapshot.Tail.Span);
        }
        await body.FlushAsync(context.RequestAborted);
    }

    /// <summary>
    /// No newer answer can exist before the next refresh, so a client, or a shared cache such as a CDN, may keep this
    /// one until the refresh is due without asking again. max-age counts down to that moment, rounded down, and is 0
    /// once it has passed: then a cache asks again with the ETag, and while the stories have not changed it gets a
    /// 304. A 304 carries the countdown too, so the cache can keep its copy until the following refresh.
    /// </summary>
    private static string CacheControl(StorySnapshot snapshot, TimeProvider clock)
    {
        var maxAge = Math.Max(0, (long)(snapshot.NextRefreshDue - clock.GetUtcNow()).TotalSeconds);
        var header = s_cacheControl;
        if (header.MaxAge != maxAge)
            s_cacheControl = header = new CacheControlHeader(maxAge, string.Create(CultureInfo.InvariantCulture, $"public, max-age={maxAge}"));
        return header.Value;
    }

    private sealed record CacheControlHeader(long MaxAge, string Value);

    /// <summary>If-None-Match holds our tag, or "*". Weak comparison, as RFC 9110 asks of If-None-Match.</summary>
    private static bool Matches(StringValues ifNoneMatch, string etag)
    {
        foreach (var value in ifNoneMatch)
        {
            if (value is null) continue;
            if (value.Trim() == "*" || value.AsSpan().IndexOf(etag.AsSpan(), StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// n must be a whole number of at least 1. More than there are returns them all, and that includes a number
    /// too big for an int, which is read as "all of them".
    /// </summary>
    private static bool TryReadN(StringValues values, out int n)
    {
        n = 0;
        if (values.Count == 0)
            return false;
        if (int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n >= 1;
        if (BigInteger.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var big) && big > int.MaxValue)
        {
            n = int.MaxValue;
            return true;
        }
        return false;
    }
}
