using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>Row backing a login attempt; contains the stored password hash.</summary>
public sealed record CredentialUser(
    Guid Id,
    string Username,
    string PasswordHash,
    UserRole Role,
    bool Active,
    int FailedAttempts,
    DateTimeOffset? LockedUntil,
    bool MustChangePassword,
    string? Email);

/// <summary>Outcome of looking up a password-reset token.</summary>
public sealed record PasswordResetLookup(
    Guid UserId,
    string Username,
    DateTimeOffset ExpiresAt,
    bool Used,
    bool UserActive,
    bool UserMustChangePassword);

public sealed partial class SqliteMonitoringStore
{
    private const string UserColumns = """
        id, username, role, active, created_at, last_login_at,
        email, failed_attempts, locked_until, must_change_password
        """;

    // ---- user accounts ------------------------------------------------------

    public IReadOnlyList<UserAccount> GetUsers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM users ORDER BY username COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var users = new List<UserAccount>();
        while (reader.Read())
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public UserAccount? GetUser(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {UserColumns} FROM users WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public CredentialUser? GetCredentialByUsername(string username)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, username, password_hash, role, active,
                   failed_attempts, locked_until, must_change_password, email
            FROM users WHERE username = $username COLLATE NOCASE;
            """;
        AddText(command, "$username", username.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CredentialUser(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            (UserRole)reader.GetInt32(3),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5),
            ReadDate(reader, 6),
            reader.GetInt32(7) != 0,
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    /// <summary>Returns null on success, or a human-readable error.</summary>
    public string? CreateUser(string username, string? email, string passwordHash, UserRole role)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO users (id, username, password_hash, role, active, created_at, last_login_at, email)
                VALUES ($id, $username, $hash, $role, 1, $created_at, NULL, $email);
                """;
            AddText(command, "$id", Guid.NewGuid().ToString());
            AddText(command, "$username", username.Trim());
            AddText(command, "$hash", passwordHash);
            command.Parameters.AddWithValue("$role", (int)role);
            AddText(command, "$created_at", FormatDate(DateTimeOffset.UtcNow));
            AddText(command, "$email", email?.Trim());
            try
            {
                command.ExecuteNonQuery();
                return null;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                return "That username already exists.";
            }
        }
    }

    public UserAccount? UpdateUser(Guid id, UserRole? role, bool? active, string? email)
    {
        lock (_writeGate)
        {
            var existing = GetUser(id);
            if (existing is null)
            {
                return null;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users SET role = $role, active = $active, email = $email WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            command.Parameters.AddWithValue("$role", (int)(role ?? existing.Role));
            command.Parameters.AddWithValue("$active", (active ?? existing.Active) ? 1 : 0);
            // null = unchanged, "" = cleared, value = set
            AddText(command, "$email", email is null ? existing.Email : email.Trim());
            command.ExecuteNonQuery();
            return GetUser(id);
        }
    }

    public bool SetPasswordHash(Guid id, string passwordHash)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id;";
            AddGuid(command, "$id", id);
            AddText(command, "$hash", passwordHash);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void TouchLastLogin(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET last_login_at = $at WHERE id = $id;";
            AddGuid(command, "$id", id);
            AddText(command, "$at", FormatDate(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public int CountUsers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ---- failed logins / lockout -------------------------------------------

    /// <summary>Increments the failure counter and locks the account once the limit is reached.</summary>
    public void RegisterFailedLogin(Guid id, int maxFailures, TimeSpan lockout)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users
                SET failed_attempts = failed_attempts + 1,
                    locked_until = CASE WHEN failed_attempts + 1 >= $max_failures THEN $locked_until ELSE locked_until END
                WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            command.Parameters.AddWithValue("$max_failures", maxFailures);
            AddText(command, "$locked_until", FormatDate(DateTimeOffset.UtcNow.Add(lockout)));
            command.ExecuteNonQuery();
        }
    }

    public void ClearFailedLogins(Guid id, bool mustChangePassword = false)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users
                SET failed_attempts = 0, locked_until = NULL, must_change_password = $must_change
                WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            command.Parameters.AddWithValue("$must_change", mustChangePassword ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public void SetMustChangePassword(Guid id, bool value)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE users SET must_change_password = $value WHERE id = $id;";
            AddGuid(command, "$id", id);
            command.Parameters.AddWithValue("$value", value ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Clears lockout state after an administrator intervenes.</summary>
    public bool UnlockUser(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users SET failed_attempts = 0, locked_until = NULL WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---- password recovery --------------------------------------------------

    /// <summary>
    /// Creates a single-use recovery token and stores only its SHA-256 hash.
    /// Returns the raw token (shown/emailed once) and its expiry.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) CreatePasswordResetToken(
        Guid userId,
        TimeSpan lifetime,
        string delivery,
        string requestedBy,
        string? requestedIp)
    {
        lock (_writeGate)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.Add(lifetime);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            // one live token per user: supersede anything outstanding
            using (var invalidate = connection.CreateCommand())
            {
                invalidate.Transaction = transaction;
                invalidate.CommandText = """
                    UPDATE password_resets SET used_at = $now
                    WHERE user_id = $user_id AND used_at IS NULL;
                    """;
                AddText(invalidate, "$now", FormatDate(now));
                AddText(invalidate, "$user_id", userId.ToString());
                invalidate.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO password_resets
                        (token_hash, user_id, created_at, expires_at, used_at, delivery, requested_by, requested_ip)
                    VALUES ($token, $user_id, $created_at, $expires_at, NULL, $delivery, $requested_by, $requested_ip);
                    """;
                AddText(insert, "$token", HashToken(token));
                AddText(insert, "$user_id", userId.ToString());
                AddText(insert, "$created_at", FormatDate(now));
                AddText(insert, "$expires_at", FormatDate(expiresAt));
                AddText(insert, "$delivery", delivery);
                AddText(insert, "$requested_by", requestedBy);
                AddText(insert, "$requested_ip", requestedIp);
                insert.ExecuteNonQuery();
            }

            // opportunistic cleanup of long-expired rows
            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM password_resets WHERE expires_at < $cutoff;";
                AddText(prune, "$cutoff", FormatDate(now.AddDays(-7)));
                prune.ExecuteNonQuery();
            }

            transaction.Commit();
            return (token, expiresAt);
        }
    }

    public PasswordResetLookup? FindPasswordReset(string token)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.user_id, r.expires_at, r.used_at, u.username, u.active, u.must_change_password
            FROM password_resets r
            JOIN users u ON u.id = r.user_id
            WHERE r.token_hash = $token;
            """;
        AddText(command, "$token", HashToken(token));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PasswordResetLookup(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
            !reader.IsDBNull(2),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0);
    }

    public void MarkPasswordResetUsed(string token)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE password_resets SET used_at = $now WHERE token_hash = $token AND used_at IS NULL;
                """;
            AddText(command, "$now", FormatDate(DateTimeOffset.UtcNow));
            AddText(command, "$token", HashToken(token));
            command.ExecuteNonQuery();
        }
    }

    // ---- sessions -----------------------------------------------------------

    /// <summary>Stores the SHA-256 of the opaque session token, never the token itself.</summary>
    public void CreateSession(string tokenHash, Guid userId, DateTimeOffset expiresAt)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions (token_hash, user_id, created_at, expires_at)
                VALUES ($token, $user_id, $created_at, $expires_at);
                """;
            AddText(command, "$token", tokenHash);
            AddText(command, "$user_id", userId.ToString());
            AddText(command, "$created_at", FormatDate(DateTimeOffset.UtcNow));
            AddText(command, "$expires_at", FormatDate(expiresAt));
            command.ExecuteNonQuery();
        }
    }

    public UserAccount? GetSessionUser(string tokenHash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {PrefixedUserColumns("u")} FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token_hash = $token AND s.expires_at >= $now AND u.active = 1;";
        AddText(command, "$token", tokenHash);
        AddText(command, "$now", FormatDate(DateTimeOffset.UtcNow));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public void DeleteSession(string tokenHash)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE token_hash = $token;";
            AddText(command, "$token", tokenHash);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Signs a user out everywhere (used after recovery or an admin security action).</summary>
    public int RevokeAllSessions(Guid userId)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE user_id = $user_id;";
            AddText(command, "$user_id", userId.ToString());
            return command.ExecuteNonQuery();
        }
    }

    public int PruneExpiredSessions()
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE expires_at < $now;";
            AddText(command, "$now", FormatDate(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        }
    }

    // ---- audit log ----------------------------------------------------------

    public void AppendAudit(
        string action,
        string actorName,
        Guid? actorUserId,
        string entityType,
        string entityId,
        string details,
        string? ipAddress)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit_log
                    (id, occurred_at, actor_user_id, actor_name, action, entity_type, entity_id, details, ip_address)
                VALUES
                    ($id, $occurred_at, $actor_user_id, $actor_name, $action, $entity_type, $entity_id, $details, $ip_address);
                """;
            AddText(command, "$id", Guid.NewGuid().ToString());
            AddText(command, "$occurred_at", FormatDate(DateTimeOffset.UtcNow));
            AddText(command, "$actor_user_id", actorUserId?.ToString());
            AddText(command, "$actor_name", actorName);
            AddText(command, "$action", action);
            AddText(command, "$entity_type", entityType);
            AddText(command, "$entity_id", entityId);
            AddText(command, "$details", details);
            AddText(command, "$ip_address", ipAddress);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AuditEntry> GetAuditEntries(int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, occurred_at, actor_name, actor_user_id, action, entity_type, entity_id, details, ip_address
            FROM audit_log
            ORDER BY occurred_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var reader = command.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (reader.Read())
        {
            entries.Add(new AuditEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                OccurredAt = ReadDate(reader, 1) ?? DateTimeOffset.MinValue,
                ActorName = reader.GetString(2),
                ActorUserId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                Action = reader.GetString(4),
                EntityType = reader.GetString(5),
                EntityId = reader.GetString(6),
                Details = reader.GetString(7),
                IpAddress = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return entries;
    }

    /// <summary>One-way hash of an opaque session token or reset token for storage/lookup.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string PrefixedUserColumns(string prefix) =>
        string.Join(", ", UserColumns
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(column => $"{prefix}.{column}"));

    private static UserAccount ReadUser(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Username = reader.GetString(1),
        Role = (UserRole)reader.GetInt32(2),
        Active = reader.GetInt32(3) != 0,
        CreatedAt = ReadDate(reader, 4) ?? DateTimeOffset.MinValue,
        LastLoginAt = ReadDate(reader, 5),
        Email = reader.IsDBNull(6) ? null : reader.GetString(6),
        FailedAttempts = reader.GetInt32(7),
        LockedUntil = ReadDate(reader, 8),
        MustChangePassword = reader.GetInt32(9) != 0
    };
}
