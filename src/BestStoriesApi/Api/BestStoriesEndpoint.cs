using System.Globalization;
using System.Text.Json;
using BestStoriesApi.Stories;

namespace BestStoriesApi.Api;

/// <summary>GET /api/beststories?n=10 : the n highest-scoring best stories, highest first.</summary>
public static class BestStoriesEndpoint
{
    public const string Route = "/api/beststories";

    public static IEndpointConventionBuilder MapBestStories(this IEndpointRouteBuilder app) =>
        app.MapGet(Route, HandleAsync)
            .WithName("GetBestStories")
            .WithSummary("The n highest-scoring of Hacker News's best stories, highest first.")
            .Produces<IEnumerable<Story>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task HandleAsync(HttpContext context, StoryStore store)
    {
        if (!TryReadN(context.Request.Query["n"], out var n))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Query parameter 'n' is required and must be a whole number of at least 1.").ExecuteAsync(context);
            return;
        }

        var snapshot = await store.GetAsync(context.RequestAborted);
        if (snapshot is null)
        {
            context.Response.Headers.RetryAfter = "5";
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Stories are not available yet. Try again shortly.").ExecuteAsync(context);
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, snapshot.Take(n), StoryJsonContext.Default.IEnumerableStory, context.RequestAborted);
    }

    /// <summary>n must be a whole number of at least 1. More than there are returns them all.</summary>
    private static bool TryReadN(Microsoft.Extensions.Primitives.StringValues values, out int n)
    {
        n = 0;
        return values.Count > 0
            && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
            && n >= 1;
    }
}
