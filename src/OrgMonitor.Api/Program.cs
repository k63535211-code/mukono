using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OrgMonitor.Api.Endpoints;
using OrgMonitor.Api.Models;
using OrgMonitor.Api.Services;
using OrgMonitor.Api.Services.Email;
using OrgMonitor.Api.Services.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

builder.Services.AddCors(options => options.AddPolicy("frontend", policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddHttpClient("monitoring", client => client.Timeout = TimeSpan.FromSeconds(6));
builder.Services.AddHttpClient("webhook", client => client.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddSingleton<SqliteMonitoringStore>();
builder.Services.AddSingleton<MonitoringEventBus>();
builder.Services.AddSingleton<AccessPolicy>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NotificationDispatcher>());
builder.Services.AddHostedService<MonitoringWorker>();

var app = builder.Build();

app.UseCors("frontend");

// Serve the built Vite dashboard (web/dist) when present, with SPA fallback for
// deep links such as /reset-password?token=...
var distDirectory = FindDistDirectory(app.Environment);
if (distDirectory is not null)
{
    var fileProvider = new PhysicalFileProvider(distDirectory);
    var indexFile = Path.Combine(distDirectory, "index.html");

    app.Use(async (context, next) =>
    {
        await next();
        if (context.Response.StatusCode == 404 &&
            !context.Request.Path.StartsWithSegments("/api") &&
            !context.Request.Path.StartsWithSegments("/health") &&
            File.Exists(indexFile))
        {
            context.Response.StatusCode = 200;
            await context.Response.SendFileAsync(indexFile, context.RequestAborted);
        }
    });
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.MapApiEndpoints();
app.Run();

static string? FindDistDirectory(IHostEnvironment environment)
{
    var candidates = new[]
    {
        Path.Combine(environment.ContentRootPath, "wwwroot"),
        Path.Combine(environment.ContentRootPath, "..", "..", "web", "dist"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        Path.Combine(AppContext.BaseDirectory, "web", "dist")
    };

    foreach (var candidate in candidates)
    {
        try
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, "index.html")))
            {
                return full;
            }
        }
        catch (Exception)
        {
            // candidate path not usable; try the next one
        }
    }

    return null;
}

/// <summary>Exposes the entry point to WebApplicationFactory-based tests.</summary>
public partial class Program;
