using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace TerminalHelper.Api.Assistant;

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "claude";

    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (IOptions<AssistantOptions> options) => new HealthResponse(
            Model: options.Value.Model,
            Credentials: HasCredentials(options.Value)));

        api.MapPost("/ask", Results<ServerSentEventsResult<string>, ValidationProblem> (
            AskRequest request, ClaudeAssistant assistant, HttpContext http) =>
        {
            var errors = RequestValidation.Validate(request);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            return TypedResults.ServerSentEvents(assistant.Ask(request, http.RequestAborted));
        }).RequireRateLimiting(RateLimitPolicy);

        api.MapPost("/explain", Results<ServerSentEventsResult<string>, ValidationProblem> (
            ExplainRequest request, ClaudeAssistant assistant, HttpContext http) =>
        {
            var errors = RequestValidation.Validate(request);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            return TypedResults.ServerSentEvents(assistant.Explain(request, http.RequestAborted));
        }).RequireRateLimiting(RateLimitPolicy);

        // Unknown API routes get a real 404 instead of falling through to the SPA's index.html.
        api.Map("/{**rest}", () => TypedResults.NotFound());
    }

    /// <summary>
    /// A hint for the UI so it can show setup instructions up front. The SDK can also pick up an
    /// <c>ant auth login</c> profile, which lives under ~/.config/anthropic.
    /// </summary>
    static bool HasCredentials(AssistantOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApiKey)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))
        || Directory.Exists(Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));

    sealed record HealthResponse(string Model, bool Credentials);
}
