using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrgMonitor.Api.Models;
using OrgMonitor.Api.Services.Auth;
using OrgMonitor.Api.Services.Email;

namespace OrgMonitor.Api.Services;

public sealed class AuthService
{
    /// <summary>Verified when the supplied username does not exist, to equalize login timing.</summary>
    private static readonly string DummyHash = PasswordHasher.Hash("timing-equalizer");

    private readonly SqliteMonitoringStore _store;
    private readonly AccessPolicy _access;
    private readonly EmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthService> _logger;

    // ip -> earliest next allowed recovery request (cheap self-service throttling)
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recoveryThrottle = new();

    public AuthService(
        SqliteMonitoringStore store,
        AccessPolicy access,
        EmailSender email,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<AuthService> logger)
    {
        _store = store;
        _access = access;
        _email = email;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    private TimeSpan ResetLifetime =>
        TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("Auth:ResetTokenMinutes", 30), 5, 1440));

    private int MaxFailedLogins => Math.Clamp(_configuration.GetValue("Auth:MaxFailedLogins", 5), 3, 50);

    private TimeSpan LockoutDuration =>
        TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("Auth:LockoutMinutes", 15), 1, 1440));

    /// <summary>Creates the initial administrator when no users exist yet.</summary>
    public void Bootstrap()
    {
        if (_store.CountUsers() > 0)
        {
            return;
        }

        var bootstrapPassword = _configuration["Auth:BootstrapPassword"];
        if (_environment.IsDevelopment())
        {
            _store.CreateUser("admin", null, PasswordHasher.Hash("admin"), UserRole.admin);
            _logger.LogWarning(
                "Created development user 'admin' with the default password 'admin'. Change it before production use.");
        }
        else if (!string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            _store.CreateUser("admin", null, PasswordHasher.Hash(bootstrapPassword), UserRole.admin);
            _logger.LogWarning(
                "Created bootstrap user 'admin' from Auth:BootstrapPassword. Change the password after first sign-in.");
        }
        else
        {
            _logger.LogWarning(
                "No user accounts exist. Configure Auth:BootstrapPassword to create an initial administrator.");
        }
    }

    // ---- sign in / out ------------------------------------------------------

    public (UserAccount? User, string? Error) Login(HttpContext context, string? username, string? password)
    {
        var credential = string.IsNullOrWhiteSpace(username)
            ? null
            : _store.GetCredentialByUsername(username);
        var ip = AccessPolicy.ClientAddress(context);

        if (credential is not null && credential.LockedUntil is { } lockedUntil &&
            lockedUntil > DateTimeOffset.UtcNow)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
            return (null, $"Too many failed sign-ins. This account is locked until {lockedUntil:u}.");
        }

        var valid = credential is not null &&
                    !string.IsNullOrWhiteSpace(password) &&
                    PasswordHasher.Verify(password, credential.PasswordHash);

        if (!valid || credential is null)
        {
            PasswordHasher.Verify(password ?? string.Empty, DummyHash);
            if (credential is not null)
            {
                _store.RegisterFailedLogin(credential.Id, MaxFailedLogins, LockoutDuration);
                _store.AppendAudit(
                    "auth.login_failed", credential.Username, credential.Id, "user", credential.Id.ToString(),
                    "Failed sign-in attempt.", ip);
                _logger.LogWarning("Failed sign-in for {User} from {Ip}", credential.Username, ip);
            }

            return (null, "Invalid username or password.");
        }

        if (!credential.Active)
        {
            return (null, "This account is disabled.");
        }

        var user = _store.GetUser(credential.Id);
        if (user is null)
        {
            return (null, "Invalid username or password.");
        }

        _store.ClearFailedLogins(user.Id, mustChangePassword: user.MustChangePassword);
        _store.TouchLastLogin(user.Id);
        _access.CreateSession(context, user);
        _store.AppendAudit(
            "auth.login", user.Username, user.Id, "user", user.Id.ToString(),
            "Signed in.", ip);
        return (_store.GetUser(user.Id), null);
    }

    public void Logout(HttpContext context, ApiPrincipal? principal)
    {
        _access.ClearSession(context);
        if (principal?.UserId is { } userId)
        {
            _store.AppendAudit(
                "auth.logout", principal.Actor, userId, "user", userId.ToString(),
                "Signed out.", AccessPolicy.ClientAddress(context));
        }
    }

    public string? ChangePassword(
        HttpContext context,
        ApiPrincipal principal,
        string? currentPassword,
        string? newPassword)
    {
        if (principal.UserId is not { } userId)
        {
            return "Changing a password requires a signed-in user account.";
        }

        var passwordError = RequestValidation.ValidatePassword(newPassword, 10);
        if (passwordError is not null)
        {
            return passwordError;
        }

        var account = _store.GetUser(userId);
        if (account is null)
        {
            return "Account not found.";
        }

        var credential = _store.GetCredentialByUsername(account.Username);
        if (credential is null)
        {
            return "Account not found.";
        }

        if (!PasswordHasher.Verify(currentPassword ?? string.Empty, credential.PasswordHash))
        {
            return "The current password is incorrect.";
        }

        _store.SetPasswordHash(userId, PasswordHasher.Hash(newPassword!));
        _store.SetMustChangePassword(userId, false);
        _store.AppendAudit(
            "auth.password_changed", principal.Actor, userId, "user", userId.ToString(),
            "Password changed.", AccessPolicy.ClientAddress(context));
        return null;
    }

    // ---- password recovery --------------------------------------------------

    /// <summary>
    /// Step 1: accepts a username, emails a single-use reset link when possible.
    /// Always succeeds from the caller's perspective so usernames cannot be enumerated.
    /// </summary>
    public void RequestRecovery(HttpContext context, string? username)
    {
        var ip = AccessPolicy.ClientAddress(context);

        // throttle per client address so the endpoint cannot be used to spam tokens
        var now = DateTimeOffset.UtcNow;
        if (_recoveryThrottle.TryGetValue(ip, out var notBefore) && now < notBefore)
        {
            _logger.LogDebug("Recovery request from {Ip} throttled", ip);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var credential = _store.GetCredentialByUsername(username);
        if (credential is null || !credential.Active)
        {
            _logger.LogInformation("Recovery requested for unknown/disabled account from {Ip}", ip);
            return;
        }

        var account = _store.GetUser(credential.Id);
        if (account is null)
        {
            return;
        }

        var (token, expiresAt) = _store.CreatePasswordResetToken(
            account.Id, ResetLifetime, delivery: "email", requestedBy: "self-service", requestedIp: ip);

        var resetLink = BuildResetLink(context, token);
        var sent = _email.SendPasswordReset(account.Email, account.Username, resetLink, expiresAt);

        _store.AppendAudit(
            "auth.recovery_requested", account.Username, account.Id, "user", account.Id.ToString(),
            sent ? "Reset link emailed." : "Reset token created but not delivered (see server log).", ip);

        // clients hammering the endpoint get a short cooldown regardless of outcome
        _recoveryThrottle[ip] = now.AddSeconds(30);
    }

    /// <summary>Step 2: exchanges the token for a new password and locks the attacker out.</summary>
    public string? ConfirmRecovery(HttpContext context, string? token, string? newPassword)
    {
        if (RequestValidation.ValidateResetToken(token) is { } tokenError)
        {
            return tokenError;
        }

        if (RequestValidation.ValidatePassword(newPassword, 10) is { } passwordError)
        {
            return passwordError;
        }

        var lookup = _store.FindPasswordReset(token!);
        if (lookup is null || lookup.Used || lookup.ExpiresAt < DateTimeOffset.UtcNow || !lookup.UserActive)
        {
            return "This reset link is invalid or has expired.";
        }

        var ip = AccessPolicy.ClientAddress(context);
        _store.SetPasswordHash(lookup.UserId, PasswordHasher.Hash(newPassword!));
        _store.MarkPasswordResetUsed(token!);
        _store.ClearFailedLogins(lookup.UserId, mustChangePassword: false);
        // revoke every session: if an attacker holds one, they lose it here
        var revoked = _store.RevokeAllSessions(lookup.UserId);
        _store.SetMustChangePassword(lookup.UserId, false);

        _store.AppendAudit(
            "auth.recovery_completed", lookup.Username, lookup.UserId, "user", lookup.UserId.ToString(),
            $"Password reset via recovery link; revoked {revoked} session(s).", ip);
        _logger.LogInformation("Password recovered for {User}; revoked {Count} session(s)", lookup.Username, revoked);
        return null;
    }

    /// <summary>Administrator path: mint a token the admin delivers out of band.</summary>
    public (string? Token, DateTimeOffset? ExpiresAt, string? Error) IssueResetToken(
        HttpContext context, ApiPrincipal principal, Guid targetUserId)
    {
        var user = _store.GetUser(targetUserId);
        if (user is null)
        {
            return (null, null, "User not found.");
        }

        var ip = AccessPolicy.ClientAddress(context);
        var (token, expiresAt) = _store.CreatePasswordResetToken(
            targetUserId, ResetLifetime, delivery: "admin",
            requestedBy: principal.Actor, requestedIp: ip);

        _store.AppendAudit(
            "user.reset_token_issued", principal.Actor, principal.UserId, "user", targetUserId.ToString(),
            $"Issued a password reset token for {user.Username}.", ip);
        return (token, expiresAt, null);
    }

    /// <summary>Administrator path: force-sign-out everywhere (response to a suspected compromise).</summary>
    public int RevokeSessions(HttpContext context, ApiPrincipal principal, Guid targetUserId)
    {
        var user = _store.GetUser(targetUserId);
        var revoked = _store.RevokeAllSessions(targetUserId);
        _store.AppendAudit(
            "user.sessions_revoked", principal.Actor, principal.UserId, "user", targetUserId.ToString(),
            $"Revoked {revoked} session(s) for {user?.Username ?? targetUserId.ToString()}.",
            AccessPolicy.ClientAddress(context));
        return revoked;
    }

    public string BuildResetLink(HttpContext context, string token)
    {
        var configuredBase = _configuration["Auth:PublicBaseUrl"];
        var baseUrl = string.IsNullOrWhiteSpace(configuredBase)
            ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}"
            : configuredBase.TrimEnd('/');
        return $"{baseUrl}/reset-password?token={token}";
    }
}
