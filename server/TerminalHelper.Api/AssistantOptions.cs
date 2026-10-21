namespace TerminalHelper.Api;

/// <summary>
/// Bound from the "Anthropic" section: appsettings.json, user-secrets, or env vars such as
/// <c>Anthropic__Model</c>. The API key falls back to the <c>ANTHROPIC_API_KEY</c> env var.
/// </summary>
public sealed class AssistantOptions
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

    public int MaxTokens { get; set; } = 4096;

    /// <summary>Per client IP. Every request costs money, so keep a lid on runaway loops.</summary>
    public int RequestsPerMinute { get; set; } = 30;
}
