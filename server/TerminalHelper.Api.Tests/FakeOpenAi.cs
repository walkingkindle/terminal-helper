using System.Net;
using System.Text;
using System.Text.Json;

namespace TerminalHelper.Api.Tests;

/// <summary>Stands in for OpenAI and for a local model server: both speak the chat completions API.</summary>
public sealed class FakeOpenAi : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri Uri, string? Authorization, JsonElement? Body, long? ContentLength);

    public List<Request> Requests { get; } = [];

    public List<Request> ChatRequests => Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions")).ToList();

    public Func<HttpResponseMessage> RespondToChat { get; set; } =
        () => Stream("""{"answer":"","commands":[{"command":"pwd","label":"Print working directory","where":"","explanation":"","parts":[]}],"note":""}""");

    public Func<HttpResponseMessage> RespondToModels { get; set; } = () => Json(new
    {
        @object = "list",
        data = new[] { new { id = "gpt-5.4-mini" }, new { id = "gpt-5.4-nano" }, new { id = "qwen2.5-coder:7b" } },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? (JsonElement?)null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        Requests.Add(new Request(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body, request.Content?.Headers.ContentLength));
        return request.RequestUri!.AbsolutePath.EndsWith("/models") ? RespondToModels() : RespondToChat();
    }

    /// <summary>A chat completions stream: content deltas, a finish reason, a usage chunk, then [DONE].</summary>
    public static HttpResponseMessage Stream(string text, string finishReason = "stop", string? refusal = null)
    {
        var sse = new StringBuilder();
        void Data(object chunk) => sse.Append($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        object Choice(object delta, string? finish = null) => new { index = 0, delta, finish_reason = finish };

        Data(new { id = "chatcmpl-1", @object = "chat.completion.chunk", choices = new[] { Choice(new { role = "assistant", content = "" }) } });
        for (var i = 0; i < text.Length; i += 9)
            Data(new { choices = new[] { Choice(new { content = text.Substring(i, Math.Min(9, text.Length - i)) }) } });
        if (refusal is not null) Data(new { choices = new[] { Choice(new { refusal }) } });
        Data(new { choices = new[] { Choice(new { }, finishReason) } });
        Data(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 111, completion_tokens = 22, total_tokens = 133 } });
        sse.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string message, string? code = null) =>
        Json(new { error = new { message, type = "invalid_request_error", code } }, status);

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
