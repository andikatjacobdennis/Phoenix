using System.Net.Mime;
using Phoenix.DemoApp;

var builder = WebApplication.CreateBuilder(args);

// Phoenix passes the listening address through the release manifest arguments, so a company
// can move the application to another port without rebuilding it.
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5080");

var app = builder.Build();
var logger = app.Logger;

var simulation = DemoInfo.Simulation;

// The rollback demonstration needs an application that really fails, not a pretend verdict.
if (simulation == "crash")
{
    logger.LogError(
        "{Variable}=crash: exiting immediately so Phoenix sees a startup failure.",
        DemoInfo.SimulationVariable);

    return 3;
}

var info = new DemoInfo(
    Company: app.Configuration["Demo:CompanyName"] ?? "Example Company",
    Product: app.Configuration["Demo:ProductName"] ?? "Phoenix Demo Application",
    Version: DemoInfo.ReadVersion(),
    Environment: app.Environment.EnvironmentName,
    MachineName: Environment.MachineName,
    StartedAt: DateTimeOffset.Now,
    InstallationPath: AppContext.BaseDirectory);

// The endpoint Phoenix polls before it accepts a version as known good.
app.MapGet("/health", () =>
{
    if (simulation == "unhealthy")
    {
        logger.LogWarning("{Variable}=unhealthy: reporting 503.", DemoInfo.SimulationVariable);

        return Results.Json(
            new { status = "Unhealthy", reason = "Simulated failure for the Phoenix rollback demo." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Json(new
    {
        status = "Healthy",
        version = info.Version,
        uptimeSeconds = (int)(DateTimeOffset.Now - info.StartedAt).TotalSeconds,
    });
});

app.MapGet("/api/info", () => Results.Json(info));

app.MapGet("/", () => Results.Content(RenderPage(info), MediaTypeNames.Text.Html));

logger.LogInformation(
    "{Product} {Version} started in {Environment} from {Path}.",
    info.Product,
    info.Version,
    info.Environment,
    info.InstallationPath);

app.Run();
return 0;

static string RenderPage(DemoInfo info)
{
    static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{Encode(info.Product)}}</title>
          <style>
            :root { color-scheme: light dark; --accent: #1f6feb; --muted: #6b7280; }
            body {
              margin: 0; min-height: 100vh; display: grid; place-items: center;
              font: 16px/1.5 "Segoe UI", system-ui, sans-serif;
              background: Canvas; color: CanvasText;
            }
            main { width: min(34rem, 92vw); padding: 2rem 0; }
            h1 { font-size: 1.6rem; margin: 0 0 .25rem; }
            p.lead { margin: 0 0 2rem; color: var(--muted); }
            dl { display: grid; grid-template-columns: auto 1fr; gap: .6rem 1.5rem; margin: 0 0 2rem; }
            dt { color: var(--muted); }
            dd { margin: 0; font-variant-numeric: tabular-nums; }
            .ok {
              display: inline-flex; align-items: center; gap: .5rem;
              padding: .6rem .9rem; border-radius: .5rem;
              background: color-mix(in srgb, green 12%, transparent);
              border: 1px solid color-mix(in srgb, green 35%, transparent);
            }
            .dot { width: .55rem; height: .55rem; border-radius: 50%; background: green; }
            footer { margin-top: 2rem; font-size: .85rem; color: var(--muted); }
            a { color: var(--accent); }
          </style>
        </head>
        <body>
          <main>
            <h1>{{Encode(info.Product)}}</h1>
            <p class="lead">{{Encode(info.Company)}}</p>

            <dl>
              <dt>Version</dt><dd>{{Encode(info.Version)}}</dd>
              <dt>Environment</dt><dd>{{Encode(info.Environment)}}</dd>
              <dt>Computer</dt><dd>{{Encode(info.MachineName)}}</dd>
              <dt>Started</dt><dd>{{info.StartedAt.ToString("HH:mm:ss")}}</dd>
            </dl>

            <div class="ok"><span class="dot"></span> Installation successful.</div>

            <footer>
              Installed and launched by Phoenix.
              <a href="/health">Health</a> &middot; <a href="/api/info">Details</a>
            </footer>
          </main>
        </body>
        </html>
        """;
}
