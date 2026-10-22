namespace TerminalHelper.Api;

/// <summary>Bound from "Assistant": limits that apply whichever AI provider answers.</summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public int MaxTokens { get; set; } = 4096;

    /// <summary>Per client IP. Every request costs money, so keep a lid on runaway loops.</summary>
    public int RequestsPerMinute { get; set; } = 30;
}

/// <summary>
/// Bound from "Anthropic": appsettings.json, user-secrets, or env vars such as <c>Anthropic__Model</c>.
/// A key saved in the app wins over <see cref="ApiKey"/>, which wins over ANTHROPIC_API_KEY.
/// </summary>
public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>low | medium | high. Lower is faster and terser.</summary>
    public string Effort { get; set; } = "low";

    /// <summary>
    /// Adaptive thinking makes the model pause before the first token. Off by default:
    /// command lookups don't need it and the answer should start streaming immediately.
    /// </summary>
    public bool Thinking { get; set; }
}
