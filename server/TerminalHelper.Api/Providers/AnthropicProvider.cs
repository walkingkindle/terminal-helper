using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace TerminalHelper.Api.Providers;

/// <summary>Creates SDK clients; tests swap this for one that talks to a fake Anthropic.</summary>
public interface IClaudeClientFactory
{
    /// <summary>A null key leaves the SDK to find credentials itself (an `ant auth login` profile).</summary>
    AnthropicClient Create(string? apiKey);
}

public sealed class ClaudeClientFactory : IClaudeClientFactory
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public AnthropicClient Create(string? apiKey) => apiKey is null
        ? new AnthropicClient { Timeout = Timeout }
        : new AnthropicClient { ApiKey = apiKey, Timeout = Timeout };
}

/// <summary>Claude through the official Anthropic SDK, with structured output so the answer is always valid JSON.</summary>
public sealed class AnthropicProvider(IClaudeClientFactory factory, IOptions<AnthropicOptions> options) : IModelProvider
{
    readonly Lock _lock = new();
    AnthropicClient? _client;
    string? _clientKey;

    public string Id => ProviderCatalog.Anthropic.Id;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ProviderSettings settings, ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = Params(settings, request);
        long inputTokens = 0, outputTokens = 0;
        string? stopReason = null;

        IAsyncEnumerator<RawMessageStreamEvent>? events = null;
        try
        {
            while (true)
            {
                try
                {
                    // Created inside the try so a failure before the first event is reported like any other.
                    events ??= ClientFor(settings.ApiKey).Messages.CreateStreaming(parameters, ct).GetAsyncEnumerator(ct);
                    if (!await events.MoveNextAsync()) break;
                }
                catch (AnthropicException ex)
                {
                    throw Translate(ex, settings);
                }

                var current = events.Current;
                if (current.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
                {
                    yield return new TextDelta(text.Text);
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

        yield return new Finished(stopReason, inputTokens, outputTokens);
    }

    /// <summary>Listing models is free, so it's how a new key gets tried before it's saved.</summary>
    public async Task<ProviderCheck> CheckAsync(ProviderSettings settings, CancellationToken ct)
    {
        try
        {
            var page = await factory.Create(settings.ApiKey).Models.List(cancellationToken: ct);
            return new ProviderCheck(CheckOutcome.Valid, null, page.Items.Select(m => m.ID).ToList());
        }
        catch (AnthropicUnauthorizedException)
        {
            return new ProviderCheck(CheckOutcome.Rejected, "Anthropic didn't accept that key. Check it and try again.", []);
        }
        catch (AnthropicIOException)
        {
            return new ProviderCheck(CheckOutcome.Unreachable, "Couldn't reach Anthropic to check the key.", []);
        }
        catch (AnthropicException)
        {
            return new ProviderCheck(CheckOutcome.Unknown, null, []);
        }
    }

    MessageCreateParams Params(ProviderSettings settings, ModelRequest request) => new()
    {
        Model = settings.Model,
        MaxTokens = request.MaxTokens,
        System = request.System,
        Messages = request.Turns
            .Select(turn => turn.Role == ChatRole.User
                ? (MessageParam)new UserMessageParam { Content = turn.Text }
                : new AssistantMessageParam { Content = turn.Text })
            .ToList(),
        Thinking = options.Value.Thinking ? new ThinkingConfigAdaptive() : new ThinkingConfigDisabled(),
        OutputConfig = new OutputConfig
        {
            Effort = ParseEffort(options.Value.Effort),
            Format = new JsonOutputFormat { Schema = request.Schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value) },
        },
    };

    public static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        _ => throw new ArgumentException($"Unsupported effort '{value}'. Use low, medium or high."),
    };

    /// <summary>One client per key, so a key saved in the app applies to the next question.</summary>
    AnthropicClient ClientFor(string? key)
    {
        lock (_lock)
        {
            if (_client is null || key != _clientKey)
            {
                _client = factory.Create(key);
                _clientKey = key;
            }
            return _client;
        }
    }

    static ProviderException Translate(AnthropicException ex, ProviderSettings settings) => ex switch
    {
        AnthropicUnauthorizedException => new("Anthropic didn't accept the API key. Check it in Settings.", "auth"),
        AnthropicNotFoundException => new($"Anthropic has no model called “{settings.Model}”. Pick another in Settings.", "setup"),
        AnthropicRateLimitException => new("Anthropic rate limit hit. Give it a few seconds and try again.", "rate_limit"),
        Anthropic5xxException => new("Anthropic's API is having trouble right now. Try again in a moment.", "upstream"),
        AnthropicBadRequestException bad => new($"Anthropic rejected the request: {bad.Message}", "bad_request"),
        AnthropicIOException => new("Couldn't reach the Anthropic API. Check the internet connection.", "network"),
        _ => new(ex.Message, "upstream"),
    };
}
