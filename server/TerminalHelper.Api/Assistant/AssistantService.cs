using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TerminalHelper.Api.Providers;
using TerminalHelper.Api.Storage;

namespace TerminalHelper.Api.Assistant;

/// <summary>
/// Streams the active provider's structured (JSON) answer to the browser as server-sent events:
/// <c>delta</c> (raw JSON text chunks), then exactly one of <c>done</c> or <c>error</c>.
/// </summary>
public sealed class AssistantService(
    SettingsStore settings,
    IEnumerable<IModelProvider> providers,
    IOptions<AssistantOptions> options,
    ILogger<AssistantService> logger)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IAsyncEnumerable<SseItem<string>> Ask(AskRequest request, CancellationToken ct)
    {
        var turns = new List<ChatTurn>();
        foreach (var turn in (request.History ?? []).Where(IsUsable).TakeLast(RequestLimits.HistoryTurns))
        {
            turns.Add(new ChatTurn(ChatRole.User, turn!.Question!.Trim()));
            turns.Add(new ChatTurn(ChatRole.Assistant, turn.Answer!.Trim()));
        }
        turns.Add(new ChatTurn(ChatRole.User, request.Question!.Trim()));

        var model = new ModelRequest(Prompts.Ask(request.Env!, request.Verbose), turns, "answer", Schemas.Answer, options.Value.MaxTokens);
        return Run(model, "ask", ct);
    }

    public IAsyncEnumerable<SseItem<string>> Explain(ExplainRequest request, CancellationToken ct)
    {
        List<ChatTurn> turns = [new(ChatRole.User, Prompts.ExplainMessage(request.Command!))];
        var model = new ModelRequest(Prompts.Explain(request.Env!), turns, "explanation", Schemas.Explanation, options.Value.MaxTokens);
        return Run(model, "explain", ct);
    }

    static bool IsUsable(HistoryTurn? turn) =>
        turn is not null
        && !string.IsNullOrWhiteSpace(turn.Question) && turn.Question.Length <= RequestLimits.Question
        && !string.IsNullOrWhiteSpace(turn.Answer) && turn.Answer.Length <= RequestLimits.HistoryAnswer;

    async IAsyncEnumerable<SseItem<string>> Run(ModelRequest request, string kind, [EnumeratorCancellation] CancellationToken ct)
    {
        var active = settings.Active;
        if (!SettingsStore.IsReady(active))
        {
            yield return Event("error", new ErrorPayload(NotReady(active), "setup"));
            yield break;
        }

        var provider = providers.Single(p => p.Id == active.Id);
        var started = Stopwatch.GetTimestamp();
        Finished? finished = null;

        await using var events = provider.StreamAsync(active, request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ErrorPayload? error = null;
            try
            {
                if (!await events.MoveNextAsync()) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser went away (Stop button, closed window); disposing the enumerator cancels the upstream call.
                yield break;
            }
            catch (Exception ex)
            {
                error = Describe(ex, active);
            }

            if (error is not null)
            {
                yield return Event("error", error);
                yield break;
            }

            switch (events.Current)
            {
                case TextDelta delta:
                    yield return Event("delta", new DeltaPayload(delta.Text));
                    break;
                case Finished done:
                    finished = done;
                    break;
            }
        }

        logger.LogInformation("{Kind} answered by {Provider} {Model} in {Elapsed:0} ms: {Input} in / {Output} out tokens, stop={StopReason}",
            kind, active.Id, active.Model, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            finished?.InputTokens, finished?.OutputTokens, finished?.StopReason);

        yield return Event("done", new DonePayload(
            finished?.StopReason, finished?.InputTokens ?? 0, finished?.OutputTokens ?? 0, active.Id, active.Model));
    }

    static string NotReady(ProviderSettings active) => active.Info == ProviderCatalog.Local
        ? "Pick a local model server and model in Settings to start asking."
        : $"Add your {active.Info.Name} API key in Settings to start asking.";

    ErrorPayload Describe(Exception ex, ProviderSettings active)
    {
        ErrorPayload payload = ex switch
        {
            ProviderException known => new(known.Message, known.Code),
            HttpRequestException => new($"Couldn't reach {active.Info.Name}.", "network"),
            JsonException => new($"{active.Info.Name} sent an answer this app couldn't read.", "upstream"),
            _ => new("Something went wrong on the server.", "internal"),
        };
        logger.LogWarning(ex, "{Provider} request failed ({Code})", active.Id, payload.Code);
        return payload;
    }

    static SseItem<string> Event(string type, object payload) => new(JsonSerializer.Serialize(payload, Json), type);

    sealed record DeltaPayload(string Text);

    sealed record DonePayload(string? StopReason, long InputTokens, long OutputTokens, string Provider, string Model);

    sealed record ErrorPayload(string Message, string Code);
}
