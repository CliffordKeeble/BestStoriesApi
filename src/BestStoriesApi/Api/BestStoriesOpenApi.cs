using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BestStoriesApi.Api;

/// <summary>
/// Describes the query parameter <c>n</c> for Swagger. The endpoint reads <c>n</c> itself rather than through
/// parameter binding, to keep the request path minimal, so Swagger cannot see it without this.
/// Used only where Swagger is (Development); it does nothing on the request path.
/// </summary>
public sealed class BestStoriesOpenApi : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals("/" + context.ApiDescription.RelativePath, BestStoriesEndpoint.Route, StringComparison.OrdinalIgnoreCase))
            return;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "n",
            In = ParameterLocation.Query,
            Required = true,
            Description = "How many stories to return. At least 1. More than exist returns them all.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1" },
        });
    }
}
