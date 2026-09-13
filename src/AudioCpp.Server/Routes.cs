using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// The HTTP surface, matching audio.cpp's server.
/// </summary>
/// <remarks>
/// Response shapes follow the reference server's rather than anything more
/// convenient: the point of serving this API is that a client written against
/// audio.cpp works unchanged, and a field renamed for taste breaks that
/// silently.
/// </remarks>
internal static class Routes
{
    public static void Map(WebApplication app, ModelPool pool, AudioCppServer server,
                           Action<string> log)
    {
        app.MapGet("/health", () =>
        {
            log("GET /health");
            return Results.Json(new
            {
                status = "ok",
                models = pool.Models.Count,
            });
        });

        // OpenAI's model list shape: clients iterate data[] and read id.
        app.MapGet("/v1/models", () =>
        {
            log("GET /v1/models");
            return Results.Json(new
            {
                @object = "list",
                data = pool.Models.Select(m => new
                {
                    id = m.Id,
                    @object = "model",
                    owned_by = "audio.cpp",
                    task = m.Task,
                    mode = m.Mode,
                }).ToArray(),
            });
        });
    }
}
