using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TerminalHelper.Api.Storage;

namespace TerminalHelper.Api.Assistant;

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "claude";

    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (IOptions<AssistantOptions> options, ApiKeyStore keys) => new HealthResponse(
            Model: options.Value.Model,
            Credentials: keys.Current.Key is not null || HasOtherCredentials()));

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
    /// Besides an API key, the SDK can use an auth token or an <c>ant auth login</c> profile
    /// (under ~/.config/anthropic). Only a hint for the UI's "add your key" prompt.
    /// </summary>
    static bool HasOtherCredentials() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))
        || Directory.Exists(Environment.GetEnvironmentVariable("ANTHROPIC_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));

    sealed record HealthResponse(string Model, bool Credentials);
}
