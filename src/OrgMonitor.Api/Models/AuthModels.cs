namespace OrgMonitor.Api.Models;

/// <summary>Serialized as lowercase role names ("admin", "operator", "viewer").</summary>
public enum UserRole
{
    admin = 0,
    @operator = 1,
    viewer = 2
}

public sealed class UserAccount
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public UserRole Role { get; init; } = UserRole.viewer;
    public bool Active { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }
    /// <summary>Recovery address; optional, but required for self-service password recovery.</summary>
    public string? Email { get; init; }
    /// <summary>Consecutive failed sign-ins since the last success.</summary>
    public int FailedAttempts { get; init; }
    /// <summary>Set after too many failed sign-ins; login is rejected until this passes.</summary>
    public DateTimeOffset? LockedUntil { get; init; }
    /// <summary>When true the user must change their password before any write action.</summary>
    public bool MustChangePassword { get; init; }
}

public sealed record LoginRequest(string? Username, string? Password);

public sealed record CreateUserRequest(string? Username, string? Password, UserRole Role, string? Email);

public sealed record UpdateUserRequest(UserRole? Role, bool? Active, string? Email);

/// <summary>Step 1 of recovery: always answered generically so usernames cannot be enumerated.</summary>
public sealed record PasswordRecoveryRequest(string? Username);

/// <summary>Step 2 of recovery: exchanges a single-use token for a new password.</summary>
public sealed record PasswordRecoveryConfirm(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record LoginResult(UserAccount User);

public sealed class AuditEntry
{
    public Guid Id { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string ActorName { get; init; } = string.Empty;
    public Guid? ActorUserId { get; init; }
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
}
