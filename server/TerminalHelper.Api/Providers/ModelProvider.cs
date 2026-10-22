using System.Text.Json;

namespace TerminalHelper.Api.Providers;

/// <summary>An AI service the app can ask. Settings, defaults and wording hang off its id.</summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    bool RequiresKey,
    bool EditableBaseUrl,
    string? KeyEnvironmentVariable,
    string ConfigSection);

public static class ProviderCatalog
{
    public static readonly ProviderInfo Anthropic = new("anthropic", "Claude (Anthropic)", true, false, "ANTHROPIC_API_KEY", "Anthropic");
    public static readonly ProviderInfo OpenAI = new("openai", "OpenAI", true, false, "OPENAI_API_KEY", "OpenAI");
    /// <summary>Anything that speaks OpenAI's chat completions API, usually a model on this computer.</summary>
    public static readonly ProviderInfo Local = new("local", "Local model", false, true, null, "Local");

    public static readonly IReadOnlyList<ProviderInfo> All = [Anthropic, OpenAI, Local];

    public static ProviderInfo? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

public enum KeySource
{
    None,
    /// <summary>Entered in the app or passed to the launcher with --api-key; kept in settings.json.</summary>
    Saved,
    /// <summary>From appsettings, user-secrets or an env var such as OpenAI__ApiKey.</summary>
    Config,
    /// <summary>The provider's usual variable, ANTHROPIC_API_KEY or OPENAI_API_KEY.</summary>
    Environment,
}

/// <summary>What a provider needs for one request: the effective key, model and server.</summary>
public sealed record ProviderSettings(ProviderInfo Info, string? ApiKey, KeySource KeySource, string Model, string? BaseUrl)
{
    public string Id => Info.Id;
}

public enum ChatRole
{
    User,
    Assistant,
}

public sealed record ChatTurn(ChatRole Role, string Text);

/// <summary>A provider-neutral request: answer in JSON that matches <see cref="Schema"/>.</summary>
public sealed record ModelRequest(string System, IReadOnlyList<ChatTurn> Turns, string SchemaName, JsonElement Schema, int MaxTokens);

public abstract record ModelEvent;

public sealed record TextDelta(string Text) : ModelEvent;

/// <summary><paramref name="StopReason"/> uses Anthropic's words: end_turn, max_tokens, refusal.</summary>
public sealed record Finished(string? StopReason, long InputTokens, long OutputTokens) : ModelEvent;

/// <summary>A failure worth showing as is; <see cref="Code"/> lets the UI react (auth → open Settings).</summary>
public sealed class ProviderException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public enum CheckOutcome
{
    Valid,
    Rejected,
    /// <summary>The server couldn't be reached at all.</summary>
    Unreachable,
    /// <summary>Reached, but the answer didn't settle it either way (an outage, an odd server).</summary>
    Unknown,
}

public sealed record ProviderCheck(CheckOutcome Outcome, string? Message, IReadOnlyList<string> Models);

public interface IModelProvider
{
    string Id { get; }

    /// <summary>Streams the answer text (JSON per the request's schema), then one <see cref="Finished"/>.</summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ProviderSettings settings, ModelRequest request, CancellationToken ct);

    /// <summary>Tries a key / server before it's saved, and lists the models it offers.</summary>
    Task<ProviderCheck> CheckAsync(ProviderSettings settings, CancellationToken ct);
}
