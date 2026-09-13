using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AudioCpp.Server;

/// <summary>
/// Server-sent events, in the shape OpenAI's streaming clients read.
/// </summary>
/// <remarks>
/// Written straight to the response body rather than through an IResult,
/// because the response has to start before the work finishes — that is the
/// whole point of the route, and an IResult is a value returned after it.
///
/// Every event is flushed as it is written. Without that, a chunk sits in
/// Kestrel's buffer until enough of them accumulate, and a stream whose
/// deltas arrive in bursts is indistinguishable from one that is simply slow —
/// which is the property a client is streaming to get.
/// </remarks>
internal sealed class Sse(HttpResponse response)
{
    /// <summary>
    /// Wraps a response whose stream has already started, to report a failure
    /// through it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Begin"/> because Begin sets the status and the
    /// headers, which throws once a byte has been written — so using it to
    /// report a mid-stream failure destroys the connection instead of
    /// explaining it, and the client sees "response ended prematurely" rather
    /// than the error. Which is the failure the error event exists to prevent.
    /// </remarks>
    public static Sse Attach(HttpResponse response) => new(response);

    public static Sse Begin(HttpResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        // Proxies buffer event streams by default and there is no way to tell
        // from the client whether that is happening; these are the headers that
        // ask them not to.
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        return new Sse(response);
    }

    public async Task SendAsync(object payload, CancellationToken cancel)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await WriteAsync($"data: {json}\n\n", cancel);
    }

    /// <summary>The terminator OpenAI clients stop on.</summary>
    public Task DoneAsync(CancellationToken cancel) => WriteAsync("data: [DONE]\n\n", cancel);

    /// <summary>
    /// An error after the stream has started, which cannot be a status code
    /// because the status was sent with the first byte.
    /// </summary>
    /// <remarks>
    /// A client that sees this instead of <c>[DONE]</c> knows the stream failed
    /// rather than ended. Closing the connection silently would be
    /// indistinguishable from a finished response, which is the failure mode
    /// worth avoiding: a truncated transcript that looks complete.
    /// </remarks>
    public Task ErrorAsync(string message, string type, CancellationToken cancel) =>
        SendAsync(new { type = "error", error = new { message, type } }, cancel);

    private async Task WriteAsync(string text, CancellationToken cancel)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancel);
        await response.Body.FlushAsync(cancel);
    }

    /// <remarks>
    /// Pinned rather than left implicit. Including nulls is already the
    /// default, but it is load-bearing here and a default can be changed
    /// globally: a null <c>ttft_ms</c> is the live routes' way of saying output
    /// began before input ended, so dropping the field would turn a deliberate
    /// answer into a missing one.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
