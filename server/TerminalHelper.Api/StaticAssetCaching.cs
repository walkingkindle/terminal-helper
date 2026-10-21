using System.Text.RegularExpressions;

namespace TerminalHelper.Api;

public static partial class StaticAssetCaching
{
    /// <summary>
    /// The Angular build (client → wwwroot) names bundles like <c>main-ABCD1234.js</c>. Their content never
    /// changes under the same name, so browsers may keep them for a year. Everything else, index.html
    /// included, keeps the revalidating defaults so a new deploy is picked up on the next load.
    /// </summary>
    public static IApplicationBuilder UseImmutableCachingForHashedBundles(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (IsHashedBundle(http.Request.Path))
            {
                http.Response.OnStarting(() =>
                {
                    if (http.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status304NotModified)
                        http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    return Task.CompletedTask;
                });
            }
            await next();
        });

    public static bool IsHashedBundle(PathString path) => path.HasValue && HashedBundle().IsMatch(path.Value);

    [GeneratedRegex(@"^/[\w-]+-[A-Z0-9]{8}\.(?:js|css)$")]
    private static partial Regex HashedBundle();
}
