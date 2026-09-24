using System.Globalization;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>Result of persisting a probe, used by the alert engine to detect transitions.</summary>
public sealed record ProbePersistence(bool Found, HealthStatus PreviousStatus, string Name);

public sealed partial class SqliteMonitoringStore
{
    private const string TargetColumns = """
        id, name, address, port, protocol, kind, enabled, status,
        latency_ms, last_checked, notes, interval_seconds, last_error
        """;

    public IReadOnlyList<NetworkTarget> GetNetworkTargets()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TargetColumns} FROM network_targets ORDER BY name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var targets = new List<NetworkTarget>();
        while (reader.Read())
        {
            targets.Add(ReadTarget(reader));
        }

        return targets;
    }

    public NetworkTarget? GetNetworkTarget(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TargetColumns} FROM network_targets WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTarget(reader) : null;
    }

    public NetworkTarget AddNetworkTarget(CreateNetworkTargetRequest request)
    {
        lock (_writeGate)
        {
            var target = new NetworkTarget
            {
                Id = Guid.NewGuid(),
                Name = request.Name?.Trim() ?? "New target",
                Address = request.Address?.Trim() ?? string.Empty,
                Port = request.Port,
                Protocol = RequestValidation.NormalizeProtocol(request.Protocol),
                Kind = request.Kind,
                Enabled = true,
                IntervalSeconds = Math.Clamp(request.IntervalSeconds ?? 60, 5, 86_400),
                Notes = request.Notes?.Trim() ?? string.Empty
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO network_targets
                    (id, name, address, port, protocol, kind, enabled, interval_seconds,
                     status, latency_ms, last_checked, last_error, notes)
                VALUES
                    ($id, $name, $address, $port, $protocol, $kind, 1, $interval_seconds,
                     $status, NULL, NULL, NULL, $notes);
                """;
            AddText(command, "$id", target.Id.ToString());
            AddText(command, "$name", target.Name);
            AddText(command, "$address", target.Address);
            command.Parameters.AddWithValue("$port", target.Port);
            AddText(command, "$protocol", target.Protocol);
            command.Parameters.AddWithValue("$kind", (int)target.Kind);
            command.Parameters.AddWithValue("$interval_seconds", target.IntervalSeconds);
            command.Parameters.AddWithValue("$status", (int)HealthStatus.Unknown);
            AddText(command, "$notes", target.Notes);
            command.ExecuteNonQuery();

            return target;
        }
    }

    public NetworkTarget? UpdateNetworkTarget(Guid id, UpdateNetworkTargetRequest request)
    {
        lock (_writeGate)
        {
            var existing = GetNetworkTarget(id);
            if (existing is null)
            {
                return null;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE network_targets
                SET name = $name, address = $address, port = $port, protocol = $protocol,
                    kind = $kind, enabled = $enabled, interval_seconds = $interval_seconds, notes = $notes
                WHERE id = $id;
                """;
            AddGuid(command, "$id", id);
            AddText(command, "$name", string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim());
            AddText(command, "$address",
                string.IsNullOrWhiteSpace(request.Address) ? existing.Address : request.Address.Trim());
            command.Parameters.AddWithValue("$port", request.Port ?? existing.Port);
            AddText(command, "$protocol",
                request.Protocol is null ? existing.Protocol : RequestValidation.NormalizeProtocol(request.Protocol));
            command.Parameters.AddWithValue("$kind", (int)(request.Kind ?? existing.Kind));
            command.Parameters.AddWithValue("$enabled", (request.Enabled ?? existing.Enabled) ? 1 : 0);
            command.Parameters.AddWithValue("$interval_seconds",
                Math.Clamp(request.IntervalSeconds ?? existing.IntervalSeconds, 5, 86_400));
            AddText(command, "$notes", request.Notes?.Trim() ?? existing.Notes);
            command.ExecuteNonQuery();

            return GetNetworkTarget(id);
        }
    }

    public bool DeleteNetworkTarget(Guid id)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM network_targets WHERE id = $id;";
            AddGuid(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Updates the target row and appends a probe history sample.</summary>
    public ProbePersistence PersistProbe(Guid targetId, HealthStatus status, double? latencyMs, string? error, DateTimeOffset checkedAt)
    {
        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT name, status FROM network_targets WHERE id = $id;";
            AddGuid(lookup, "$id", targetId);
            using (var reader = lookup.ExecuteReader())
            {
                if (!reader.Read())
                {
                    transaction.Rollback();
                    return new ProbePersistence(false, HealthStatus.Unknown, string.Empty);
                }

                var name = reader.GetString(0);
                var previous = (HealthStatus)reader.GetInt32(1);
                reader.Close();

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE network_targets
                    SET status = $status, latency_ms = $latency_ms, last_checked = $checked_at, last_error = $error
                    WHERE id = $id;
                    """;
                AddGuid(update, "$id", targetId);
                update.Parameters.AddWithValue("$status", (int)status);
                AddNullableDouble(update, "$latency_ms", latencyMs);
                AddText(update, "$checked_at", FormatDate(checkedAt));
                AddText(update, "$error", error);
                update.ExecuteNonQuery();

                using var history = connection.CreateCommand();
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO probe_results (target_id, checked_at, status, latency_ms, error)
                    VALUES ($target_id, $checked_at, $status, $latency_ms, $error);
                    """;
                AddText(history, "$target_id", targetId.ToString());
                AddText(history, "$checked_at", FormatDate(checkedAt));
                history.Parameters.AddWithValue("$status", (int)status);
                AddNullableDouble(history, "$latency_ms", latencyMs);
                AddText(history, "$error", error);
                history.ExecuteNonQuery();

                transaction.Commit();
                return new ProbePersistence(true, previous, name);
            }
        }
    }

    public IReadOnlyList<ProbeResultPoint> GetProbeHistory(Guid targetId, DateTimeOffset since)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT checked_at, status, latency_ms, error
            FROM probe_results
            WHERE target_id = $target_id AND checked_at >= $since
            ORDER BY checked_at ASC;
            """;
        AddText(command, "$target_id", targetId.ToString());
        AddText(command, "$since", FormatDate(since));

        using var reader = command.ExecuteReader();
        var points = new List<ProbeResultPoint>();
        while (reader.Read())
        {
            points.Add(new ProbeResultPoint
            {
                CheckedAt = ReadDate(reader, 0) ?? DateTimeOffset.MinValue,
                Status = (HealthStatus)reader.GetInt32(1),
                LatencyMs = ReadDouble(reader, 2),
                Error = reader.IsDBNull(3) ? null : reader.GetString(3)
            });
        }

        return points;
    }

    public AvailabilitySummary GetTargetAvailability(Guid targetId, DateTimeOffset since)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, latency_ms
            FROM probe_results
            WHERE target_id = $target_id AND checked_at >= $since;
            """;
        AddText(command, "$target_id", targetId.ToString());
        AddText(command, "$since", FormatDate(since));

        var total = 0;
        var successful = 0;
        var latencies = new List<double>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                total++;
                if ((HealthStatus)reader.GetInt32(0) == HealthStatus.Online)
                {
                    successful++;
                }

                if (!reader.IsDBNull(1))
                {
                    latencies.Add(reader.GetDouble(1));
                }
            }
        }

        latencies.Sort();
        double? average = latencies.Count == 0 ? null : latencies.Average();
        double? p95 = latencies.Count == 0
            ? null
            : latencies[(int)Math.Clamp(Math.Ceiling(latencies.Count * 0.95) - 1, 0, latencies.Count - 1)];

        return new AvailabilitySummary(
            total,
            successful,
            total == 0 ? 100 : Math.Round(successful * 100d / total, 2),
            average is null ? null : Math.Round(average.Value, 2),
            p95 is null ? null : Math.Round(p95.Value, 2));
    }

    /// <summary>Rolling success rate across every target for the requested window.</summary>
    public double? GetAvailability(TimeSpan window)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END)
            FROM probe_results
            WHERE checked_at >= $since;
            """;
        AddText(command, "$since", FormatDate(DateTimeOffset.UtcNow.Subtract(window)));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var total = reader.GetInt64(0);
        var successful = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return total == 0 ? null : Math.Round(successful * 100d / total, 2);
    }
}
