using System.Collections.Concurrent;
using System.Net;
using CodeGraph.Analysis;
using CodeGraph.Models;

// No telemetry, including from the MSBuild helper process Roslyn starts.
Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");

// Must happen before any MSBuildWorkspace type is used.
MsBuildBootstrap.Register();

var port = int.TryParse(Environment.GetEnvironmentVariable("CODEGRAPH_PORT"), out var p) ? p : 5080;

var builder = WebApplication.CreateBuilder(args);
// Loopback only: an explicit Listen overrides --urls / ASPNETCORE_URLS, so this cannot be widened by accident.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
builder.Services.AddSingleton<AnalysisService>();

var app = builder.Build();

// Refuse requests that did not come through a localhost address (DNS-rebinding protection),
// and API calls made by other web sites.
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    var origin = context.Request.Headers.Origin.ToString();
    var hostOk = host is "localhost" or "127.0.0.1";
    var originOk = origin.Length == 0 ||
                   (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "localhost" or "127.0.0.1");
    if (!hostOk || !originOk)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/analyze", async (AnalyzeRequest request, AnalysisService service, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await service.AnalyzeAsync(request, ct));
    }
    catch (InputValidationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Analysis failed: {ex.Message}" }, statusCode: 500);
    }
});

app.MapGet("/api/progress", (AnalysisService service) => Results.Ok(new { message = service.Progress }));

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"\nCodeGraph is running. Open http://localhost:{port}\n"));

app.Run();

/// <summary>Runs analyses one at a time and caches the result per path (+ test-project option).</summary>
public sealed class AnalysisService
{
    private readonly ConcurrentDictionary<string, GraphResult> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string _progress = "";

    public string Progress => _progress;

    public async Task<object> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct)
    {
        var input = InputResolver.Resolve(request.Path); // validates: existing directory or .sln
        var key = $"{input.CacheKey}|tests={!request.ExcludeTests}";

        if (!request.Refresh && _cache.TryGetValue(key, out var cached))
            return Response(cached, fromCache: true);

        await _gate.WaitAsync(ct);
        try
        {
            if (!request.Refresh && _cache.TryGetValue(key, out cached))
                return Response(cached, fromCache: true);

            _progress = "Starting…";
            // Roslyn work is CPU-bound; keep it off the request thread.
            var result = await Task.Run(
                () => SolutionAnalyzer.AnalyzeAsync(input, request.ExcludeTests, m => _progress = m, ct), ct);
            _cache[key] = result;
            return Response(result, fromCache: false);
        }
        finally
        {
            _progress = "";
            _gate.Release();
        }
    }

    private static object Response(GraphResult r, bool fromCache) => new
    {
        r.Source, r.Nodes, r.Edges, r.Communities, r.Warnings, r.Stats, r.AnalyzedAt, fromCache,
    };
}
