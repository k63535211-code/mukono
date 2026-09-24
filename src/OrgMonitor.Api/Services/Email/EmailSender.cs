using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OrgMonitor.Api.Services.Email;

/// <summary>
/// Optional SMTP delivery for password-recovery links. Uses the framework SmtpClient so the
/// API keeps zero extra package dependencies; when SMTP is not configured the feature falls
/// back to administrator-issued reset tokens.
/// </summary>
public sealed class EmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IConfiguration configuration, ILogger<EmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Smtp:Host"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Smtp:From"]);

    public bool SendPasswordReset(string? recipient, string username, string resetLink, DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            _logger.LogWarning("Password reset requested but {User} has no email address on file", username);
            return false;
        }

        if (!IsConfigured)
        {
            _logger.LogWarning(
                "Password reset requested for {User} but SMTP is not configured; ask an administrator for a reset token",
                username);
            return false;
        }

        var body = $"""
            Hello {username},

            Someone requested a password reset for your OrgMonitor account.
            Open the link below to choose a new password (valid until {expiresAt:u}):

            {resetLink}

            If you did not request this, you can ignore this email — your password has not changed.
            If you believe your account has been compromised, contact your administrator immediately;
            they can revoke all active sessions for your account.
            """;

#pragma warning disable CS0618 // SmtpClient is discouraged but keeps the API dependency-free
        try
        {
            using var client = new SmtpClient(_configuration["Smtp:Host"]!)
            {
                Port = _configuration.GetValue("Smtp:Port", 587),
                EnableSsl = _configuration.GetValue("Smtp:EnableSsl", true),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            var usernameCredential = _configuration["Smtp:Username"];
            if (!string.IsNullOrWhiteSpace(usernameCredential))
            {
                client.Credentials = new NetworkCredential(usernameCredential, _configuration["Smtp:Password"]);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(_configuration["Smtp:From"]!),
                Subject = "OrgMonitor password reset",
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(recipient);

            client.Send(message);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SMTP delivery of password reset email failed");
            return false;
        }
#pragma warning restore CS0618
    }
}
