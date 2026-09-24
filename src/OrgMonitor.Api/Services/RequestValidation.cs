using System.Globalization;
using System.Net;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>Shared request validation so endpoints and tests use identical rules.</summary>
public static class RequestValidation
{
    public static bool IsValidPercentage(double? value) =>
        value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 100);

    public static string? ValidateHeartbeat(AgentHeartbeat? heartbeat)
    {
        if (heartbeat is null)
        {
            return "A heartbeat body is required.";
        }

        if (heartbeat.DeviceId == Guid.Empty)
        {
            return "deviceId is required and must be a stable GUID.";
        }

        if (!Enum.IsDefined(typeof(DeviceKind), heartbeat.Kind))
        {
            return "kind is not a supported device type.";
        }

        if (!IsValidPercentage(heartbeat.CpuPercent) ||
            !IsValidPercentage(heartbeat.MemoryPercent) ||
            !IsValidPercentage(heartbeat.DiskPercent))
        {
            return "CPU, memory, and disk percentages must be finite values from 0 to 100.";
        }

        if (heartbeat.Name is { Length: > 120 })
        {
            return "name must be 120 characters or fewer.";
        }

        if (heartbeat.Tags is { Length: > 500 })
        {
            return "tags must be 500 characters or fewer.";
        }

        return null;
    }

    public static string? ValidateTarget(
        string? name,
        string? address,
        int port,
        string? protocol,
        DeviceKind kind,
        int? intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
        {
            return "A display name of at most 120 characters is required.";
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return "Address is required.";
        }

        var error = ValidateHost(address.Trim());
        if (error is not null)
        {
            return error;
        }

        if (port is < 1 or > 65535)
        {
            return "Port must be between 1 and 65535.";
        }

        var normalizedProtocol = (protocol ?? "tcp").Trim().ToLowerInvariant();
        if (normalizedProtocol is not ("tcp" or "http" or "https"))
        {
            return "Protocol must be tcp, http, or https.";
        }

        if (!Enum.IsDefined(typeof(DeviceKind), kind))
        {
            return "kind is not a supported device type.";
        }

        if (intervalSeconds is < 5 or > 86_400)
        {
            return "intervalSeconds must be between 5 and 86400.";
        }

        return null;
    }

    /// <summary>Validation for manually adding a device that has no reporting agent.</summary>
    public static string? ValidateDevice(
        string? name,
        string? hostname,
        string? address,
        DeviceKind kind,
        string? operatingSystem,
        string? tags)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
        {
            return "A display name of at most 120 characters is required.";
        }

        if (hostname is not null && hostname.Trim().Length > 120)
        {
            return "hostname must be 120 characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return "Address is required.";
        }

        if (address.Trim().Length > 120)
        {
            return "address must be 120 characters or fewer.";
        }

        if (ValidateHost(address.Trim()) is { } hostError)
        {
            return hostError;
        }

        if (!Enum.IsDefined(typeof(DeviceKind), kind))
        {
            return "kind is not a supported device type.";
        }

        if (operatingSystem is { Length: > 120 })
        {
            return "operatingSystem must be 120 characters or fewer.";
        }

        if (tags is { Length: > 500 })
        {
            return "tags must be 500 characters or fewer.";
        }

        return null;
    }

    public static string? ValidateHost(string address)
    {
        if (address.Contains("://", StringComparison.Ordinal))
        {
            return "Enter a host name or IP address without a URL scheme.";
        }

        var candidate = address;
        if (candidate.StartsWith('[') && candidate.EndsWith(']'))
        {
            candidate = candidate[1..^1];
        }

        if (Uri.CheckHostName(candidate) == UriHostNameType.Unknown)
        {
            return "Address must be a valid host name or IP address.";
        }

        return null;
    }

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "Username is required.";
        }

        var value = username.Trim();
        if (value.Length is < 3 or > 48)
        {
            return "Username must be between 3 and 48 characters.";
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return "Username may only contain letters, digits, '.', '_' and '-'.";
            }
        }

        return null;
    }

    public static string? ValidatePassword(string? password, int minimumLength)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "Password is required.";
        }

        if (password.Length < minimumLength)
        {
            return $"Password must be at least {minimumLength} characters.";
        }

        if (password.Length > 256)
        {
            return "Password must be 256 characters or fewer.";
        }

        return null;
    }

    public static string? ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null; // optional
        }

        var value = email.Trim();
        if (value.Length > 254)
        {
            return "email must be 254 characters or fewer.";
        }

        try
        {
            var parsed = new System.Net.Mail.MailAddress(value);
            if (!string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase))
            {
                return "email is not a valid address.";
            }
        }
        catch (FormatException)
        {
            return "email is not a valid address.";
        }

        return null;
    }

    /// <summary>Reset tokens are 32 random bytes rendered as 64 hex characters.</summary>
    public static string? ValidateResetToken(string? token) =>
        token is { Length: 64 } && token.All(Uri.IsHexDigit)
            ? null
            : "The reset token is missing or malformed.";

    public static string? ValidateWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "A webhook URL is required.";
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "The webhook URL must be an absolute URL.";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return "The webhook URL must use http or https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Embedded credentials are not allowed in webhook URLs.";
        }

        return null;
    }

    public static string NormalizeProtocol(string? protocol) =>
        (protocol ?? "tcp").Trim().ToLowerInvariant();

    public static string NormalizeHost(string address) => address.Trim();

    public static bool TryFormatDate(string? value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out parsed);
    }
}
