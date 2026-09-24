using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>Authenticated caller: an API key, a signed-in user, or the Development fallback.</summary>
public sealed record ApiPrincipal(string Actor, Guid? UserId, UserRole Role, bool MustChangePassword = false);

/// <summary>
/// Resolves and authorizes callers. Supported credentials:
/// X-Admin-Key (admin), an HttpOnly session cookie (user role), and — in Development only —
/// anonymous access. Returns null from <see cref="Require"/> when the caller is allowed.
/// </summary>
public sealed class AccessPolicy
{
    public const string SessionCookieName = "orgmonitor_session";
    public const string PrincipalItem = "orgmonitor.principal";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly SqliteMonitoringStore _store;

    public AccessPolicy(IConfiguration configuration, IHostEnvironment environment, SqliteMonitoringStore store)
    {
        _configuration = configuration;
        _environment = environment;
        _store = store;
    }

    private int SessionHours => Math.Clamp(_configuration.GetValue("Auth:SessionHours", 12), 1, 720);

    public ApiPrincipal? Resolve(HttpContext context)
    {
        if (context.Items[PrincipalItem] is ApiPrincipal cached)
        {
            return cached;
        }

        ApiPrincipal? principal = null;

        var configuredKey = _configuration["Monitoring:AdminApiKey"];
        if (!string.IsNullOrWhiteSpace(configuredKey) &&
            context.Request.Headers.TryGetValue("X-Admin-Key", out var supplied) &&
            supplied.Count == 1 &&
            FixedTimeEquals(configuredKey, supplied[0]?.ToString() ?? string.Empty))
        {
            principal = new ApiPrincipal("api-key", null, UserRole.admin);
        }
        else if (TryGetSessionToken(context, out var token))
        {
            var user = _store.GetSessionUser(SqliteMonitoringStore.HashToken(token));
            if (user is not null)
            {
                principal = new ApiPrincipal(user.Username, user.Id, user.Role, user.MustChangePassword);
            }
        }

        if (principal is null &&
            _environment.IsDevelopment() &&
            _configuration.GetValue("Auth:AllowAnonymousInDevelopment", true))
        {
            principal = new ApiPrincipal("development", null, UserRole.admin);
        }

        context.Items[PrincipalItem] = principal;
        return principal;
    }

    /// <summary>Returns null when access is granted, otherwise the error response to send.</summary>
    public IResult? Require(HttpContext context, UserRole minimum)
    {
        var principal = Resolve(context);
        if (principal is null)
        {
            // Fail closed outside Development when no admin key is configured (legacy contract).
            if (!_environment.IsDevelopment() && string.IsNullOrWhiteSpace(_configuration["Monitoring:AdminApiKey"]))
            {
                return Results.Json(
                    new { error = "Monitoring:AdminApiKey must be configured outside Development." },
                    statusCode: 503);
            }

            return Results.Unauthorized();
        }

        if (Rank(principal.Role) < Rank(minimum))
        {
            return Results.Json(
                new { error = $"This action requires the {minimum} role." },
                statusCode: 403);
        }

        // Compromised/recovered accounts must rotate their password before writing anything.
        if (principal.MustChangePassword && Rank(minimum) > Rank(UserRole.viewer) &&
            context.Request.Path != "/api/auth/password")
        {
            return Results.Json(
                new
                {
                    error = "You must change your password before continuing.",
                    code = "password_change_required"
                },
                statusCode: 403);
        }

        return null;
    }

    public string CreateSession(HttpContext context, UserAccount user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddHours(SessionHours);
        _store.CreateSession(SqliteMonitoringStore.HashToken(token), user.Id, expiresAt);

        context.Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = expiresAt
        });

        return token;
    }

    public void ClearSession(HttpContext context)
    {
        if (TryGetSessionToken(context, out var token))
        {
            _store.DeleteSession(SqliteMonitoringStore.HashToken(token));
        }

        context.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });
    }

    public static bool TryGetSessionToken(HttpContext context, out string token)
    {
        if (context.Request.Cookies.TryGetValue(SessionCookieName, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            token = value;
            return true;
        }

        token = string.Empty;
        return false;
    }

    public static string ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    private static int Rank(UserRole role) => role switch
    {
        UserRole.admin => 3,
        UserRole.@operator => 2,
        _ => 1
    };

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
