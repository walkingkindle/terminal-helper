using Microsoft.AspNetCore.Http.HttpResults;
using TerminalHelper.Api.Storage;

namespace TerminalHelper.Api.Assistant;

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "ai";

    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (SettingsStore settings) =>
        {
            var active = settings.Active;
            return new HealthResponse(active.Id, active.Model, SettingsStore.IsReady(active));
        });

        api.MapPost("/ask", Results<ServerSentEventsResult<string>, ValidationProblem> (
            AskRequest request, AssistantService assistant, HttpContext http) =>
        {
            var errors = RequestValidation.Validate(request);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            return TypedResults.ServerSentEvents(assistant.Ask(request, http.RequestAborted));
        }).RequireRateLimiting(RateLimitPolicy);

        api.MapPost("/explain", Results<ServerSentEventsResult<string>, ValidationProblem> (
            ExplainRequest request, AssistantService assistant, HttpContext http) =>
        {
            var errors = RequestValidation.Validate(request);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            return TypedResults.ServerSentEvents(assistant.Explain(request, http.RequestAborted));
        }).RequireRateLimiting(RateLimitPolicy);

        // Unknown API routes get a real 404 instead of falling through to the SPA's index.html.
        api.Map("/{**rest}", () => TypedResults.NotFound());
    }

    sealed record HealthResponse(string Provider, string Model, bool Ready);
}
