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

    public static IEndpointConventionBuilder MapBestStories(this IEndpointRouteBuilder app) =>
        app.MapMethods(Route, [HttpMethods.Get, HttpMethods.Head], HandleAsync)
            .WithName("GetBestStories")
            .WithSummary("The n highest-scoring of Hacker News's best stories, highest first.")
            .Produces<IEnumerable<Story>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static Task HandleAsync(HttpContext context, StoryStore store)
    {
        if (!TryReadN(context.Request.Query["n"], out var n))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Query parameter 'n' is required and must be a whole number of at least 1.").ExecuteAsync(context);
        }

        // The usual case: a snapshot is already there, so answer without waiting for anything.
        var snapshot = store.Current;
        return snapshot is not null ? WriteAsync(context, snapshot, n) : WaitForFirstSnapshotAsync(context, store, n);
    }

    /// <summary>
    /// Only before the first refresh has finished: wait for that one refresh, never start another, and not for
    /// longer than FirstSnapshotWait.
    /// </summary>
    private static async Task WaitForFirstSnapshotAsync(HttpContext context, StoryStore store, int n)
    {
        var limit = context.RequestServices.GetRequiredService<IOptions<HackerNewsOptions>>().Value.FirstSnapshotWait;
        StorySnapshot? snapshot;
        try
        {
            snapshot = await store.GetAsync(context.RequestAborted).WaitAsync(limit, context.RequestAborted);
        }
        catch (TimeoutException)
        {
            snapshot = null;
        }

        if (snapshot is null)
        {
            context.Response.Headers.RetryAfter = "5";
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Stories are not available yet. Try again shortly.").ExecuteAsync(context);
            return;
        }
        await WriteAsync(context, snapshot, n);
    }

    /// <summary>Copies the pre-built answer to the response. No serialisation happens here.</summary>
    private static async Task WriteAsync(HttpContext context, StorySnapshot snapshot, int n)
    {
        var response = context.Response;
        var etag = snapshot.ETag(n);
        response.Headers.ETag = etag;
        response.Headers.CacheControl = "no-cache"; // a client may keep it, but asks again; the answer is then 304

        if (Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = snapshot.Length(n);
        if (HttpMethods.IsHead(context.Request.Method))
            return; // HEAD: the same headers as GET, and no body

        var body = response.BodyWriter;
        body.Write(snapshot.Head(n).Span);
        body.Write(StorySnapshot.Tail.Span);
        await body.FlushAsync(context.RequestAborted);
    }

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
