using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using TerminalHelper.Api;
using TerminalHelper.Api.Assistant;
using TerminalHelper.Api.Providers;
using TerminalHelper.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);
// Lets a plain `dotnet run` in Production find the pre-compressed .br/.gz bundles under obj/
// (otherwise it serves empty compressed responses). Published output doesn't need it; it's a no-op there.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddOptions<AssistantOptions>()
    .Bind(builder.Configuration.GetSection(AssistantOptions.Section))
    .Validate(o => o.MaxTokens is > 0 and <= 32_000, "Assistant:MaxTokens must be between 1 and 32000.")
    .Validate(o => o.RequestsPerMinute > 0, "Assistant:RequestsPerMinute must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.Section))
    .Validate(o => o.Effort.ToLowerInvariant() is "low" or "medium" or "high", "Anthropic:Effort must be low, medium or high.")
    .ValidateOnStart();

builder.Services.AddSingleton<DataFolder>();
builder.Services.AddSingleton<NotesStore>();
builder.Services.AddSingleton<SettingsStore>();

// The AI providers: Claude through Anthropic's SDK; OpenAI and local models through the chat completions API.
builder.Services.AddSingleton<IClaudeClientFactory, ClaudeClientFactory>();
builder.Services.AddSingleton<IModelProvider, AnthropicProvider>();
builder.Services.AddSingleton<IModelProvider>(sp => new OpenAiCompatibleProvider(ProviderCatalog.OpenAI, sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<IModelProvider>(sp => new OpenAiCompatibleProvider(ProviderCatalog.Local, sp.GetRequiredService<IHttpClientFactory>()));
// A model on a laptop CPU can take minutes over a long answer; closing the request cancels it anyway.
builder.Services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName, http => http.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<AssistantService>();

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

app.Logger.LogInformation("Data folder: {DataFolder}", app.Services.GetRequiredService<DataFolder>().Path);

app.UseStatusCodePages();
app.UseRateLimiter();

app.MapAssistantEndpoints();
app.MapLocalEndpoints();

// Production serves the Angular build from wwwroot (see README); in development `ng serve` does.
app.UseImmutableCachingForHashedBundles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();

public partial class Program;
