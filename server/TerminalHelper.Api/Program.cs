using System.Threading.RateLimiting;
using Anthropic;
using Microsoft.Extensions.Options;
using TerminalHelper.Api;
using TerminalHelper.Api.Assistant;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);
// Lets a plain `dotnet run` in Production find the pre-compressed .br/.gz bundles under obj/
// (otherwise it serves empty compressed responses). Published output doesn't need it; it's a no-op there.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddOptions<AssistantOptions>()
    .Bind(builder.Configuration.GetSection(AssistantOptions.Section))
    .Validate(o => o.MaxTokens is > 0 and <= 32_000, "Anthropic:MaxTokens must be between 1 and 32000.")
    .Validate(o => o.RequestsPerMinute > 0, "Anthropic:RequestsPerMinute must be positive.")
    .Validate(o => o.Effort.ToLowerInvariant() is "low" or "medium" or "high", "Anthropic:Effort must be low, medium or high.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<AssistantOptions>>().Value;
    var timeout = TimeSpan.FromSeconds(90);
    // Leaving ApiKey unset lets the SDK fall back to ANTHROPIC_API_KEY (or an `ant auth login` profile).
    return string.IsNullOrWhiteSpace(options.ApiKey)
        ? new AnthropicClient { Timeout = timeout }
        : new AnthropicClient { ApiKey = options.ApiKey, Timeout = timeout };
});
builder.Services.AddSingleton<ClaudeAssistant>();

builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(AssistantEndpoints.RateLimitPolicy, http =>
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<AssistantOptions>>().Value.RequestsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
    });
});

var app = builder.Build();

app.UseStatusCodePages();
app.UseRateLimiter();

app.MapAssistantEndpoints();

// Production serves the Angular build from wwwroot (see README); in development `ng serve` does.
app.UseImmutableCachingForHashedBundles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();

public partial class Program;
