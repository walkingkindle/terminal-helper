using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TerminalHelper.Api.Providers;

/// <summary>
/// OpenAI's chat completions API over plain HTTP. The same code serves OpenAI itself and local servers
/// that copy its API (Ollama, LM Studio, llama.cpp, vLLM…); only the base URL and a couple of
/// parameter names differ, so the requests stay minimal enough for all of them to accept.
/// </summary>
public sealed class OpenAiCompatibleProvider(ProviderInfo info, IHttpClientFactory httpClients) : IModelProvider
{
    public const string HttpClientName = "openai-compatible";

    public string Id => info.Id;

    bool IsOpenAi => info == ProviderCatalog.OpenAI;

    /// <summary>How the provider is named in messages.</summary>
    string Name => IsOpenAi ? "OpenAI" : "The model server";

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ProviderSettings settings, ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var http = httpClients.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url(settings, "chat/completions"))
        {
            // A sized body rather than a chunked stream: some local servers can't read chunked requests.
            Content = new StringContent(JsonSerializer.Serialize(Body(settings, request)), Encoding.UTF8, "application/json"),
        };
        Authorize(message, settings);
        using var response = await SendAsync(http, message, settings, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));

        string? finishReason = null;
        long inputTokens = 0, outputTokens = 0;
        var refused = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new ProviderException(ErrorText(error) ?? $"{Name} reported an error.", "upstream");
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = Number(usage, "prompt_tokens");
                outputTokens = Number(usage, "completion_tokens");
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                    yield return new TextDelta(text);
                // Structured outputs report a refusal in its own field instead of the content.
                if (delta.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
                    refused = true;
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                finishReason = reason.GetString();
        }

        yield return new Finished(
            refused ? "refusal" : finishReason switch
            {
                "length" => "max_tokens",
                "content_filter" => "refusal",
                null => null,
                _ => "end_turn",
            },
            inputTokens,
            outputTokens);
    }

    /// <summary>GET /models: free, proves the key and the URL, and lists what can be picked.</summary>
    public async Task<ProviderCheck> CheckAsync(ProviderSettings settings, CancellationToken ct)
    {
        using var http = httpClients.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var message = new HttpRequestMessage(HttpMethod.Get, Url(settings, "models"));
        Authorize(message, settings);
        try
        {
            using var response = await http.SendAsync(message, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new ProviderCheck(CheckOutcome.Rejected, $"{Name} didn't accept that key. Check it and try again.", []);
            if (!response.IsSuccessStatusCode) return new ProviderCheck(CheckOutcome.Unknown, null, []);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var models = body.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>()
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            return new ProviderCheck(CheckOutcome.Valid, null, models);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new ProviderCheck(CheckOutcome.Unreachable, Unreachable(settings), []);
        }
        catch (JsonException)
        {
            return new ProviderCheck(CheckOutcome.Unknown, null, []);
        }
    }

    Dictionary<string, object?> Body(ProviderSettings settings, ModelRequest request)
    {
        var messages = new List<object> { new { role = "system", content = request.System } };
        messages.AddRange(request.Turns.Select(t => new { role = t.Role == ChatRole.User ? "user" : "assistant", content = t.Text }));
        return new()
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = request.SchemaName, strict = true, schema = request.Schema },
            },
            // OpenAI's current models only take max_completion_tokens; local servers know max_tokens.
            [IsOpenAi ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens,
        };
    }

    async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage message, ProviderSettings settings, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderException(Unreachable(settings), "network");
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var (detail, code) = await ReadErrorAsync(response, ct);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new ProviderException($"{Name} didn't accept the API key. Check it in Settings.", "auth"),
                HttpStatusCode.NotFound when detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true || code == "model_not_found" =>
                    new ProviderException($"{Name} has no model called “{settings.Model}”. Pick another in Settings.", "setup"),
                HttpStatusCode.NotFound =>
                    new ProviderException($"Nothing answers at {Url(settings, "chat/completions")}. Is the server URL right? (Ollama: http://localhost:11434/v1)", "setup"),
                HttpStatusCode.TooManyRequests when code == "insufficient_quota" =>
                    new ProviderException("Your OpenAI account is out of credits.", "rate_limit"),
                HttpStatusCode.TooManyRequests =>
                    new ProviderException($"{Name} rate limit hit. Give it a few seconds and try again.", "rate_limit"),
                >= HttpStatusCode.InternalServerError =>
                    new ProviderException($"{Name} is having trouble right now{(detail is null ? "" : $": {detail}")}", "upstream"),
                _ => new ProviderException($"{Name} rejected the request: {detail ?? response.ReasonPhrase}", "bad_request"),
            };
        }
    }

    static async Task<(string? Detail, string? Code)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!body.RootElement.TryGetProperty("error", out var error)) return (null, null);
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (ErrorText(error), code);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>OpenAI nests the message in an object; some servers send the error as a plain string.</summary>
    static string? ErrorText(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.String => error.GetString(),
        JsonValueKind.Object when error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
        _ => null,
    };

    string Unreachable(ProviderSettings settings) => IsOpenAi
        ? "Couldn't reach OpenAI. Check the internet connection."
        : $"Can't reach {settings.BaseUrl}. Is the model server running? (Ollama: run `ollama serve`)";

    static Uri Url(ProviderSettings settings, string path) => new($"{settings.BaseUrl!.TrimEnd('/')}/{path}");

    /// <summary>Local servers usually need no key; OpenAI always does.</summary>
    static void Authorize(HttpRequestMessage message, ProviderSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
