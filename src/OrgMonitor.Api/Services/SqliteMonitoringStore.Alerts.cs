using System.Globalization;
using Microsoft.Data.Sqlite;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

public sealed partial class SqliteMonitoringStore
{
    private const string AlertColumns = """
        id, severity, title, message, device_id, target_id, rule_id, device_name,
        created_at, acknowledged, acknowledged_by, acknowledged_at, resolved_at
        """;

    /// <summary>Open (unresolved) alerts, optionally including acknowledged ones.</summary>
    public IReadOnlyList<MonitorAlert> GetAlerts(bool includeAcknowledged = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = includeAcknowledged
            ? $"SELECT {AlertColumns} FROM alerts WHERE resolved_at IS NULL ORDER BY created_at DESC;"
            : $"SELECT {AlertColumns} FROM alerts WHERE resolved_at IS NULL AND acknowledged = 0 ORDER BY created_at DESC;";

        using var reader = command.ExecuteReader();
        var alerts = new List<MonitorAlert>();
        while (reader.Read())
        {
            alerts.Add(ReadAlert(reader));
        }

        return alerts;
    }

    public MonitorAlert? GetAlert(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AlertColumns} FROM alerts WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAlert(reader) : null;
    }

    /// <summary>
    /// Creates an alert. When <paramref name="dedupeKey"/> is supplied and an unresolved alert
    /// with the same key already exists, that alert is returned instead of a duplicate.
    /// </summary>
    public MonitorAlert CreateAlert(MonitorAlert alert, string? dedupeKey = null)
    {
        lock (_writeGate)
        {
            if (!string.IsNullOrWhiteSpace(dedupeKey))
            {
                var existing = GetOpenAlertByDedupeKey(dedupeKey);
                if (existing is not null)
                {
                    return existing;
                }
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alerts
                    (id, severity, title, message, device_id, target_id, rule_id, dedupe_key,
                     device_name, created_at, acknowledged, acknowledged_by, acknowledged_at, resolved_at)
                VALUES
                    ($id, $severity, $title, $message, $device_id, $target_id, $rule_id, $dedupe_key,
                     $device_name, $created_at, 0, NULL, NULL, NULL);
                """;
            AddText(command, "$id", alert.Id.ToString());
            AddText(command, "$severity", alert.Severity.ToString());
            AddText(command, "$title", alert.Title);
            AddText(command, "$message", alert.Message);
            AddText(command, "$device_id", alert.DeviceId?.ToString());
            AddText(command, "$target_id", alert.TargetId?.ToString());
            AddText(command, "$rule_id", alert.RuleId?.ToString());
            AddText(command, "$dedupe_key", dedupeKey);
            AddText(command, "$device_name", alert.EntityName);
            AddText(command, "$created_at", FormatDate(alert.CreatedAt));
            command.ExecuteNonQuery();

            return alert;
        }
    }

    public MonitorAlert? GetOpenAlertByDedupeKey(string dedupeKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AlertColumns} FROM alerts WHERE dedupe_key = $key AND resolved_at IS NULL;";
        AddText(command, "$key", dedupeKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAlert(reader) : null;
    }

    /// <summary>Auto-resolves every unresolved alert sharing a dedupe key; returns how many closed.</summary>
    public int ResolveAlertsByDedupeKey(string dedupeKey, DateTimeOffset resolvedAt)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE alerts
                SET resolved_at = $resolved_at
                WHERE dedupe_key = $key AND resolved_at IS NULL;
                """;
            AddText(command, "$key", dedupeKey);
            AddText(command, "$resolved_at", FormatDate(resolvedAt));
            return command.ExecuteNonQuery();
        }
    }

    public bool AcknowledgeAlert(Guid alertId, string actor)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE alerts
                SET acknowledged = 1, acknowledged_by = $actor, acknowledged_at = $at
                WHERE id = $id AND acknowledged = 0;
                """;
            AddGuid(command, "$id", alertId);
            AddText(command, "$actor", actor);
            AddText(command, "$at", FormatDate(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---- alert rules --------------------------------------------------------

    public IReadOnlyList<AlertRule> GetAlertRules()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, metric, comparator, threshold, severity, scope_kind,
                   consecutive_breaches, enabled, description, created_at
            FROM alert_rules
            ORDER BY name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var rules = new List<AlertRule>();
        while (reader.Read())
        {
            rules.Add(new AlertRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Metric = (AlertMetric)reader.GetInt32(2),
                Comparator = (AlertComparator)reader.GetInt32(3),
                Threshold = reader.GetDouble(4),
                Severity = (AlertSeverity)reader.GetInt32(5),
                ScopeKind = reader.IsDBNull(6) ? null : (DeviceKind)reader.GetInt32(6),
                ConsecutiveBreaches = reader.GetInt32(7),
                Enabled = reader.GetInt32(8) != 0,
                Description = reader.GetString(9),
                CreatedAt = ReadDate(reader, 10) ?? DateTimeOffset.MinValue
            });
        }

        return rules;
    }

    public AlertRule AddAlertRule(CreateAlertRuleRequest request)
    {
        lock (_writeGate)
        {
            var rule = new AlertRule
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Metric = request.Metric,
                Comparator = request.Comparator,
                Threshold = request.Threshold,
                Severity = request.Severity,
                ScopeKind = request.ScopeKind,
                ConsecutiveBreaches = Math.Clamp(request.ConsecutiveBreaches, 1, 20),
                Enabled = request.Enabled,
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alert_rules
                    (id, name, metric, comparator, threshold, severity, scope_kind,
                     consecutive_breaches, enabled, description, created_at)
                VALUES
                    ($id, $name, $metric, $comparator, $threshold, $severity, $scope_kind,
                     $consecutive_breaches, $enabled, $description, $created_at);
                """;
            AddText(command, "$id", rule.Id.ToString());
            AddText(command, "$name", rule.Name);
            command.Parameters.AddWithValue("$metric", (int)rule.Metric);
            command.Parameters.AddWithValue("$comparator", (int)rule.Comparator);
            command.Parameters.AddWithValue("$threshold", rule.Threshold);
            command.Parameters.AddWithValue("$severity", (int)rule.Severity);
            command.Parameters.AddWithValue("$scope_kind", rule.ScopeKind is null ? DBNull.Value : (object)(int)rule.ScopeKind.Value);
            command.Parameters.AddWithValue("$consecutive_breaches", rule.ConsecutiveBreaches);
            command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            AddText(command, "$description", rule.Description);
            AddText(command, "$created_at", FormatDate(rule.CreatedAt));
            command.ExecuteNonQuery();

            return rule;
        }
    }

    public AlertRule? UpdateAlertRule(Guid id, UpdateAlertRuleRequest request)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT id, name, metric, comparator, threshold, severity, scope_kind,
                       consecutive_breaches, enabled, description, created_at
                FROM alert_rules WHERE id = $id;
                """;
            AddGuid(select, "$id", id);
            AlertRule? existing;
            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                existing = new AlertRule
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    Metric = (AlertMetric)reader.GetInt32(2),
                    Comparator = (AlertComparator)reader.GetInt32(3),
                    Threshold = reader.GetDouble(4),
                    Severity = (AlertSeverity)reader.GetInt32(5),
                    ScopeKind = reader.IsDBNull(6) ? null : (DeviceKind)reader.GetInt32(6),
                    ConsecutiveBreaches = reader.GetInt32(7),
                    Enabled = reader.GetInt32(8) != 0,
                    Description = reader.GetString(9),
                    CreatedAt = ReadDate(reader, 10) ?? DateTimeOffset.MinValue
                };
            }

            var updated = new AlertRule
            {
                Id = existing.Id,
                Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                Metric = existing.Metric,
                Comparator = request.Comparator ?? existing.Comparator,
                Threshold = request.Threshold ?? existing.Threshold,
                Severity = request.Severity ?? existing.Severity,
                ScopeKind = request.ClearScope ? null : request.ScopeKind ?? existing.ScopeKind,
                ConsecutiveBreaches = Math.Clamp(request.ConsecutiveBreaches ?? existing.ConsecutiveBreaches, 1, 20),
                Enabled = request.Enabled ?? existing.Enabled,
                Description = request.Description?.Trim() ?? existing.Description,
                CreatedAt = existing.CreatedAt
            };

            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE alert_rules
                SET name = $name, comparator = $comparator, threshold = $threshold, severity = $severity,
                    scope_kind = $scope_kind, consecutive_breaches = $consecutive_breaches,
                    enabled = $enabled, description = $description
                WHERE id = $id;
                """;
            AddGuid(update, "$id", id);
            AddText(update, "$name", updated.Name);
            update.Parameters.AddWithValue("$comparator", (int)updated.Comparator);
            update.Parameters.AddWithValue("$threshold", updated.Threshold);
            update.Parameters.AddWithValue("$severity", (int)updated.Severity);
            update.Parameters.AddWithValue("$scope_kind",
                updated.ScopeKind is null ? DBNull.Value : (object)(int)updated.ScopeKind.Value);
            update.Parameters.AddWithValue("$consecutive_breaches", updated.ConsecutiveBreaches);
            update.Parameters.AddWithValue("$enabled", updated.Enabled ? 1 : 0);
            AddText(update, "$description", updated.Description);
            update.ExecuteNonQuery();

            return updated;
        }
    }

    public bool DeleteAlertRule(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM alert_rules WHERE id = $id;";
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ---- maintenance windows ------------------------------------------------

    public IReadOnlyList<MaintenanceWindow> GetMaintenanceWindows()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, scope, scope_id, starts_at, ends_at, reason, created_by, created_at
            FROM maintenance_windows
            ORDER BY starts_at DESC;
            """;

        using var reader = command.ExecuteReader();
        var windows = new List<MaintenanceWindow>();
        while (reader.Read())
        {
            windows.Add(new MaintenanceWindow
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Scope = (MaintenanceScope)reader.GetInt32(2),
                ScopeId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                StartsAt = ReadDate(reader, 4) ?? DateTimeOffset.MinValue,
                EndsAt = ReadDate(reader, 5) ?? DateTimeOffset.MinValue,
                Reason = reader.GetString(6),
                CreatedBy = reader.GetString(7),
                CreatedAt = ReadDate(reader, 8) ?? DateTimeOffset.MinValue
            });
        }

        return windows;
    }

    public MaintenanceWindow AddMaintenanceWindow(CreateMaintenanceWindowRequest request, string createdBy)
    {
        lock (_writeGate)
        {
            var window = new MaintenanceWindow
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Scope = request.Scope,
                ScopeId = request.Scope == MaintenanceScope.all ? null : request.ScopeId,
                StartsAt = request.StartsAt.ToUniversalTime(),
                EndsAt = request.EndsAt.ToUniversalTime(),
                Reason = request.Reason?.Trim() ?? string.Empty,
                CreatedBy = createdBy,
                CreatedAt = DateTimeOffset.UtcNow
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO maintenance_windows
                    (id, name, scope, scope_id, starts_at, ends_at, reason, created_by, created_at)
                VALUES
                    ($id, $name, $scope, $scope_id, $starts_at, $ends_at, $reason, $created_by, $created_at);
                """;
            AddText(command, "$id", window.Id.ToString());
            AddText(command, "$name", window.Name);
            command.Parameters.AddWithValue("$scope", (int)window.Scope);
            AddText(command, "$scope_id", window.ScopeId?.ToString());
            AddText(command, "$starts_at", FormatDate(window.StartsAt));
            AddText(command, "$ends_at", FormatDate(window.EndsAt));
            AddText(command, "$reason", window.Reason);
            AddText(command, "$created_by", window.CreatedBy);
            AddText(command, "$created_at", FormatDate(window.CreatedAt));
            command.ExecuteNonQuery();

            return window;
        }
    }

    public bool DeleteMaintenanceWindow(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM maintenance_windows WHERE id = $id;";
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>True when alerting should be suppressed for the entity right now.</summary>
    public bool IsMaintenanceActive(MaintenanceScope scope, Guid? scopeId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM maintenance_windows
            WHERE starts_at <= $now AND ends_at >= $now
              AND (scope = 0
                   OR (scope = $scope AND ($has_scope = 0 OR scope_id = $scope_id)));
            """;
        AddText(command, "$now", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$has_scope", scopeId is null ? 0 : 1);
        AddText(command, "$scope_id", scopeId?.ToString() ?? string.Empty);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // ---- notification channels ---------------------------------------------

    public IReadOnlyList<NotificationChannel> GetNotificationChannels()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, type, url, enabled, min_severity, created_at, last_delivery_at, last_success
            FROM notification_channels
            ORDER BY name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var channels = new List<NotificationChannel>();
        while (reader.Read())
        {
            channels.Add(new NotificationChannel
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Type = (NotificationChannelType)reader.GetInt32(2),
                Url = reader.GetString(3),
                Enabled = reader.GetInt32(4) != 0,
                MinSeverity = (AlertSeverity)reader.GetInt32(5),
                CreatedAt = ReadDate(reader, 6) ?? DateTimeOffset.MinValue,
                LastDeliveryAt = ReadDate(reader, 7),
                LastDeliverySuccess = reader.IsDBNull(8) ? null : reader.GetInt32(8) != 0
            });
        }

        return channels;
    }

    public NotificationChannel AddNotificationChannel(CreateNotificationChannelRequest request)
    {
        lock (_writeGate)
        {
            var channel = new NotificationChannel
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Type = NotificationChannelType.webhook,
                Url = request.Url!.Trim(),
                Enabled = request.Enabled,
                MinSeverity = request.MinSeverity,
                CreatedAt = DateTimeOffset.UtcNow
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notification_channels
                    (id, name, type, url, enabled, min_severity, created_at, last_delivery_at, last_success)
                VALUES
                    ($id, $name, $type, $url, $enabled, $min_severity, $created_at, NULL, NULL);
                """;
            AddText(command, "$id", channel.Id.ToString());
            AddText(command, "$name", channel.Name);
            command.Parameters.AddWithValue("$type", (int)channel.Type);
            AddText(command, "$url", channel.Url);
            command.Parameters.AddWithValue("$enabled", channel.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$min_severity", (int)channel.MinSeverity);
            AddText(command, "$created_at", FormatDate(channel.CreatedAt));
            command.ExecuteNonQuery();

            return channel;
        }
    }

    public NotificationChannel? UpdateNotificationChannel(Guid id, UpdateNotificationChannelRequest request)
    {
        lock (_writeGate)
        {
            var existing = GetNotificationChannels().FirstOrDefault(channel => channel.Id == id);
            if (existing is null)
            {
                return null;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE notification_channels
                SET name = $name, url = $url, enabled = $enabled, min_severity = $min_severity
                WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            AddText(command, "$name", string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim());
            AddText(command, "$url", string.IsNullOrWhiteSpace(request.Url) ? existing.Url : request.Url.Trim());
            command.Parameters.AddWithValue("$enabled", (request.Enabled ?? existing.Enabled) ? 1 : 0);
            command.Parameters.AddWithValue("$min_severity", (int)(request.MinSeverity ?? existing.MinSeverity));
            command.ExecuteNonQuery();

            return GetNotificationChannels().FirstOrDefault(channel => channel.Id == id);
        }
    }

    public bool DeleteNotificationChannel(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM notification_channels WHERE id = $id;";
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void RecordNotificationDelivery(Guid channelId, Guid? alertId, bool success, int? statusCode, string? error)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO notification_deliveries (channel_id, alert_id, attempted_at, success, status_code, error)
                    VALUES ($channel_id, $alert_id, $attempted_at, $success, $status_code, $error);
                    """;
                AddText(insert, "$channel_id", channelId.ToString());
                AddText(insert, "$alert_id", alertId?.ToString());
                AddText(insert, "$attempted_at", FormatDate(DateTimeOffset.UtcNow));
                insert.Parameters.AddWithValue("$success", success ? 1 : 0);
                insert.Parameters.AddWithValue("$status_code", (object?)statusCode ?? DBNull.Value);
                AddText(insert, "$error", error);
                insert.ExecuteNonQuery();
            }

            using (var channel = connection.CreateCommand())
            {
                channel.Transaction = transaction;
                channel.CommandText = """
                    UPDATE notification_channels
                    SET last_delivery_at = $at, last_success = $success
                    WHERE id = $id;
                    """;
                AddGuid(channel, "$id", channelId);
                AddText(channel, "$at", FormatDate(DateTimeOffset.UtcNow));
                channel.Parameters.AddWithValue("$success", success ? 1 : 0);
                channel.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<NotificationDelivery> GetRecentDeliveries(int limit = 50)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, channel_id, alert_id, attempted_at, success, status_code, error
            FROM notification_deliveries
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var reader = command.ExecuteReader();
        var deliveries = new List<NotificationDelivery>();
        while (reader.Read())
        {
            deliveries.Add(new NotificationDelivery
            {
                Id = reader.GetInt64(0),
                ChannelId = Guid.Parse(reader.GetString(1)),
                AlertId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                AttemptedAt = ReadDate(reader, 3) ?? DateTimeOffset.MinValue,
                Success = reader.GetInt32(4) != 0,
                StatusCode = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Error = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return deliveries;
    }

    private static MonitorAlert ReadAlert(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Severity = ParseSeverity(reader.GetString(1)),
        Title = reader.GetString(2),
        Message = reader.GetString(3),
        DeviceId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
        TargetId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
        RuleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        EntityName = reader.GetString(7),
        CreatedAt = ReadDate(reader, 8) ?? DateTimeOffset.MinValue,
        Acknowledged = reader.GetInt32(9) != 0,
        AcknowledgedBy = reader.IsDBNull(10) ? null : reader.GetString(10),
        AcknowledgedAt = ReadDate(reader, 11),
        ResolvedAt = ReadDate(reader, 12)
    };

    private static AlertSeverity ParseSeverity(string value) =>
        Enum.TryParse<AlertSeverity>(value, ignoreCase: true, out var severity) ? severity : AlertSeverity.warning;
}
