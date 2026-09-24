using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace OrgMonitor.Api.Services;

public static class ApiKeyGuard
{
    public static IResult? Validate(
        HttpContext context,
        IConfiguration configuration,
        IHostEnvironment environment,
        string configurationPath,
        string headerName)
    {
        var configuredKey = configuration[configurationPath];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return environment.IsDevelopment()
                ? null
                : Results.Json(new { error = $"{configurationPath} must be configured outside Development." }, statusCode: 503);
        }

        if (!context.Request.Headers.TryGetValue(headerName, out var suppliedValues) || suppliedValues.Count != 1)
        {
            return Results.Unauthorized();
        }

        var suppliedKey = suppliedValues[0];
        if (!FixedTimeEquals(configuredKey, suppliedKey?.ToString() ?? string.Empty))
        {
            return Results.Unauthorized();
        }

        return null;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
