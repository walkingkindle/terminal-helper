using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace TerminalHelper.Api.Assistant;

/// <summary>
/// Streams Claude's structured (JSON) answer to the browser as server-sent events:
/// <c>delta</c> (raw JSON text chunks), then exactly one of <c>done</c> or <c>error</c>.
/// </summary>
public sealed class ClaudeAssistant(AnthropicClient client, IOptions<AssistantOptions> options, ILogger<ClaudeAssistant> logger)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly AssistantOptions _options = options.Value;

    public IAsyncEnumerable<SseItem<string>> Ask(AskRequest request, CancellationToken ct)
    {
        var messages = new List<MessageParam>();
        foreach (var turn in (request.History ?? []).Where(IsUsable).TakeLast(RequestLimits.HistoryTurns))
        {
            messages.Add(new UserMessageParam { Content = turn.Question!.Trim() });
            messages.Add(new AssistantMessageParam { Content = turn.Answer!.Trim() });
        }
        messages.Add(new UserMessageParam { Content = request.Question!.Trim() });

        return Stream(Params(Prompts.Ask(request.Env!, request.Verbose), messages, Schemas.Answer), "ask", ct);
    }

    public IAsyncEnumerable<SseItem<string>> Explain(ExplainRequest request, CancellationToken ct)
    {
        List<MessageParam> messages = [new UserMessageParam { Content = Prompts.ExplainMessage(request.Command!) }];
        return Stream(Params(Prompts.Explain(request.Env!), messages, Schemas.Explanation), "explain", ct);
    }

    static bool IsUsable(HistoryTurn? turn) =>
        turn is not null
        && !string.IsNullOrWhiteSpace(turn.Question) && turn.Question.Length <= RequestLimits.Question
        && !string.IsNullOrWhiteSpace(turn.Answer) && turn.Answer.Length <= RequestLimits.HistoryAnswer;

    MessageCreateParams Params(string system, List<MessageParam> messages, Dictionary<string, JsonElement> schema) => new()
    {
        Model = _options.Model,
        MaxTokens = _options.MaxTokens,
        System = system,
        Messages = messages,
        Thinking = _options.Thinking ? new ThinkingConfigAdaptive() : new ThinkingConfigDisabled(),
        OutputConfig = new OutputConfig
        {
            Effort = ParseEffort(_options.Effort),
            Format = new JsonOutputFormat { Schema = schema },
        },
    };

    public static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => throw new ArgumentException($"Unsupported effort '{value}'. Use low, medium or high."),
    };

    async IAsyncEnumerable<SseItem<string>> Stream(MessageCreateParams parameters, string kind, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        long inputTokens = 0, outputTokens = 0;
        string? stopReason = null;

        IAsyncEnumerator<RawMessageStreamEvent>? events = null;
        try
        {
            while (true)
            {
                ErrorPayload? error = null;
                try
                {
                    // Created inside the try so a failure before the first event is reported like any other.
                    events ??= client.Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
                    if (!await events.MoveNextAsync()) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The browser went away (Stop button, closed tab); disposing the enumerator cancels the upstream call.
                    yield break;
                }
                catch (Exception ex)
                {
                    error = MapError(ex);
                }

                if (error is not null)
                {
                    yield return Event("error", error);
                    yield break;
                }

                var current = events!.Current; // set by the try above, or we'd have returned
                if (current.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
                {
                    yield return Event("delta", new DeltaPayload(text.Text));
                }
                else if (current.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (current.TryPickDelta(out var delta))
                {
                    stopReason = delta.Delta.StopReason?.Raw();
                    outputTokens = delta.Usage.OutputTokens;
                }
            }
        }
        finally
        {
            if (events is not null) await events.DisposeAsync();
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        logger.LogInformation("{Kind} answered in {Elapsed:0} ms: {Input} in / {Output} out tokens, stop={StopReason}",
            kind, elapsed.TotalMilliseconds, inputTokens, outputTokens, stopReason);

        yield return Event("done", new DonePayload(stopReason, inputTokens, outputTokens));
    }

    ErrorPayload MapError(Exception ex)
    {
        ErrorPayload payload = ex switch
        {
            AnthropicUnauthorizedException => new("No valid Anthropic API key. Set ANTHROPIC_API_KEY or the Anthropic:ApiKey user secret (see README).", "auth"),
            AnthropicRateLimitException => new("Anthropic rate limit hit. Give it a few seconds and try again.", "rate_limit"),
            Anthropic5xxException => new("Anthropic's API is having trouble right now. Try again in a moment.", "upstream"),
            AnthropicBadRequestException bad => new($"Anthropic rejected the request: {bad.Message}", "bad_request"),
            AnthropicIOException => new("Couldn't reach the Anthropic API. Check the server's internet connection.", "network"),
            AnthropicException other => new(other.Message, "upstream"),
            _ => new("Something went wrong on the server.", "internal"),
        };
        logger.LogWarning(ex, "Claude request failed ({Code})", payload.Code);
        return payload;
    }

    static SseItem<string> Event(string type, object payload) => new(JsonSerializer.Serialize(payload, Json), type);

    sealed record DeltaPayload(string Text);

    sealed record DonePayload(string? StopReason, long InputTokens, long OutputTokens);

    sealed record ErrorPayload(string Message, string Code);
}
