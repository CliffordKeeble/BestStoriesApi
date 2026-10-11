using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BestStoriesApi.Api;

/// <summary>
/// Writes a health report as one flat JSON object: the overall status, then the description and data of the checks
/// that ran, for example <c>{"status":"Healthy","description":"...","stories":200,"builtAt":"...","ageSeconds":42}</c>.
/// </summary>
internal static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());
            foreach (var entry in report.Entries.Values)
            {
                if (entry.Description is not null)
                    json.WriteString("description", entry.Description);

                foreach (var (key, value) in entry.Data)
                {
                    switch (value)
                    {
                        case int number: json.WriteNumber(key, number); break;
                        case long number: json.WriteNumber(key, number); break;
                        case DateTimeOffset time: json.WriteString(key, time); break;
                        default: json.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                    }
                }
            }
            json.WriteEndObject();
        }

        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(buffer.WrittenMemory).AsTask();
    }
}
