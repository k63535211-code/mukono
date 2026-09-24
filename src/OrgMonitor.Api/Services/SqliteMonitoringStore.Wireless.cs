using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

public sealed partial class SqliteMonitoringStore
{
    private const string ApColumns = """
        id, name, site, band, channel, clients, utilization_percent, noise_floor_dbm,
        snr_db, firmware, controller, status, last_seen, notes
        """;

    public IReadOnlyList<WirelessAccessPoint> GetAccessPoints()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ApColumns} FROM wireless_aps ORDER BY name COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var accessPoints = new List<WirelessAccessPoint>();
        while (reader.Read())
        {
            accessPoints.Add(ReadAccessPoint(reader));
        }

        return accessPoints;
    }

    public WirelessAccessPoint? GetAccessPoint(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ApColumns} FROM wireless_aps WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccessPoint(reader) : null;
    }

    /// <summary>
    /// Merges a controller snapshot into the inventory and appends history samples.
    /// OrgMonitor only stores what an authorized collector explicitly pushes.
    /// </summary>
    public WirelessIngestResult UpsertAccessPoints(
        IReadOnlyList<WirelessApSnapshot> snapshots,
        string? controller,
        DateTimeOffset capturedAt)
    {
        if (snapshots.Count == 0)
        {
            return new WirelessIngestResult(0, 0, capturedAt);
        }

        lock (_writeGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var updated = 0;

            foreach (var snapshot in snapshots)
            {
                var name = string.IsNullOrWhiteSpace(snapshot.Name) ? "Unnamed AP" : snapshot.Name.Trim();
                var site = snapshot.Site?.Trim() ?? string.Empty;
                var id = ResolveApId(snapshot.Id, site, name);
                var existing = ReadApRow(connection, transaction, id);

                var effective = new WirelessAccessPoint
                {
                    Id = id,
                    Name = name,
                    Site = site,
                    Band = snapshot.Band?.Trim() ?? existing?.Band ?? string.Empty,
                    Channel = snapshot.Channel ?? existing?.Channel ?? 0,
                    Clients = snapshot.Clients ?? existing?.Clients,
                    UtilizationPercent = snapshot.UtilizationPercent ?? existing?.UtilizationPercent,
                    NoiseFloorDbm = snapshot.NoiseFloorDbm ?? existing?.NoiseFloorDbm,
                    SnrDb = snapshot.SnrDb ?? existing?.SnrDb,
                    Firmware = snapshot.Firmware?.Trim() ?? existing?.Firmware ?? string.Empty,
                    Controller = !string.IsNullOrWhiteSpace(controller)
                        ? controller.Trim()
                        : existing?.Controller ?? string.Empty,
                    Status = snapshot.Status ?? existing?.Status ?? HealthStatus.Unknown,
                    LastSeen = capturedAt,
                    Notes = snapshot.Notes?.Trim() ?? existing?.Notes ?? string.Empty
                };

                using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO wireless_aps
                        (id, name, site, band, channel, clients, utilization_percent, noise_floor_dbm,
                         snr_db, firmware, controller, status, last_seen, notes)
                    VALUES
                        ($id, $name, $site, $band, $channel, $clients, $utilization, $noise,
                         $snr, $firmware, $controller, $status, $last_seen, $notes)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name,
                        site = excluded.site,
                        band = excluded.band,
                        channel = excluded.channel,
                        clients = excluded.clients,
                        utilization_percent = excluded.utilization_percent,
                        noise_floor_dbm = excluded.noise_floor_dbm,
                        snr_db = excluded.snr_db,
                        firmware = excluded.firmware,
                        controller = excluded.controller,
                        status = excluded.status,
                        last_seen = excluded.last_seen,
                        notes = excluded.notes;
                    """;
                AddText(upsert, "$id", effective.Id.ToString());
                AddText(upsert, "$name", effective.Name);
                AddText(upsert, "$site", effective.Site);
                AddText(upsert, "$band", effective.Band);
                upsert.Parameters.AddWithValue("$channel", effective.Channel);
                upsert.Parameters.AddWithValue("$clients", (object?)effective.Clients ?? DBNull.Value);
                AddNullableDouble(upsert, "$utilization", effective.UtilizationPercent);
                AddNullableDouble(upsert, "$noise", effective.NoiseFloorDbm);
                AddNullableDouble(upsert, "$snr", effective.SnrDb);
                AddText(upsert, "$firmware", effective.Firmware);
                AddText(upsert, "$controller", effective.Controller);
                upsert.Parameters.AddWithValue("$status", (int)effective.Status);
                AddText(upsert, "$last_seen", FormatDate(effective.LastSeen));
                AddText(upsert, "$notes", effective.Notes);
                upsert.ExecuteNonQuery();

                using var history = connection.CreateCommand();
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO wireless_ap_history
                        (ap_id, captured_at, clients, utilization_percent, noise_floor_dbm, snr_db)
                    VALUES ($ap_id, $captured_at, $clients, $utilization, $noise, $snr);
                    """;
                AddText(history, "$ap_id", effective.Id.ToString());
                AddText(history, "$captured_at", FormatDate(capturedAt));
                history.Parameters.AddWithValue("$clients", (object?)effective.Clients ?? DBNull.Value);
                AddNullableDouble(history, "$utilization", effective.UtilizationPercent);
                AddNullableDouble(history, "$noise", effective.NoiseFloorDbm);
                AddNullableDouble(history, "$snr", effective.SnrDb);
                history.ExecuteNonQuery();

                updated++;
            }

            transaction.Commit();
            return new WirelessIngestResult(snapshots.Count, updated, capturedAt);
        }
    }

    public IReadOnlyList<WirelessApHistoryPoint> GetAccessPointHistory(Guid id, DateTimeOffset since)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT captured_at, clients, utilization_percent, noise_floor_dbm, snr_db
            FROM wireless_ap_history
            WHERE ap_id = $id AND captured_at >= $since
            ORDER BY captured_at ASC;
            """;
        AddText(command, "$id", id.ToString());
        AddText(command, "$since", FormatDate(since));

        using var reader = command.ExecuteReader();
        var points = new List<WirelessApHistoryPoint>();
        while (reader.Read())
        {
            points.Add(new WirelessApHistoryPoint
            {
                CapturedAt = ReadDate(reader, 0) ?? DateTimeOffset.MinValue,
                Clients = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                UtilizationPercent = ReadDouble(reader, 2),
                NoiseFloorDbm = ReadDouble(reader, 3),
                SnrDb = ReadDouble(reader, 4)
            });
        }

        return points;
    }

    public WirelessSummary GetWirelessSummary()
    {
        var accessPoints = GetAccessPoints();
        var withUtilization = accessPoints
            .Where(accessPoint => accessPoint.UtilizationPercent.HasValue)
            .ToArray();

        var busyChannels = withUtilization
            .Where(accessPoint => accessPoint.Channel > 0)
            .GroupBy(accessPoint => accessPoint.Channel)
            .Where(group => group.Average(accessPoint => accessPoint.UtilizationPercent!.Value) >= 70)
            .Count();

        return new WirelessSummary(
            accessPoints.Count,
            accessPoints.Count(accessPoint => accessPoint.Status == HealthStatus.Online),
            accessPoints.Count(accessPoint => accessPoint.Status == HealthStatus.Warning),
            accessPoints.Count(accessPoint => accessPoint.Status == HealthStatus.Offline),
            accessPoints.Sum(accessPoint => accessPoint.Clients ?? 0),
            withUtilization.Length == 0
                ? null
                : Math.Round(withUtilization.Average(accessPoint => accessPoint.UtilizationPercent!.Value), 1),
            busyChannels,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Access points not reported within the threshold become offline; returns the changed ones.</summary>
    public IReadOnlyList<(Guid Id, string Name)> MarkStaleAccessPointsOffline(TimeSpan staleAfter)
    {
        lock (_writeGate)
        {
            var cutoff = FormatDate(DateTimeOffset.UtcNow.Subtract(staleAfter));
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT id, name FROM wireless_aps
                WHERE status <> $offline AND last_seen < $cutoff;
                """;
            lookup.Parameters.AddWithValue("$offline", (int)HealthStatus.Offline);
            AddText(lookup, "$cutoff", cutoff);

            var stale = new List<(Guid Id, string Name)>();
            using (var reader = lookup.ExecuteReader())
            {
                while (reader.Read())
                {
                    stale.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
                }
            }

            foreach (var accessPoint in stale)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE wireless_aps SET status = $offline WHERE id = $id;";
                update.Parameters.AddWithValue("$offline", (int)HealthStatus.Offline);
                AddGuid(update, "$id", accessPoint.Id);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return stale;
        }
    }

    private WirelessAccessPoint? ReadApRow(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ApColumns} FROM wireless_aps WHERE id = $id;";
        AddGuid(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccessPoint(reader) : null;
    }

    /// <summary>Uses the supplied identifier when it is a GUID, otherwise derives a stable one.</summary>
    private static Guid ResolveApId(string? supplied, string site, string name)
    {
        if (!string.IsNullOrWhiteSpace(supplied) && Guid.TryParse(supplied, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        var seed = supplied ?? $"{site}/{name}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("orgmonitor-ap:" + seed));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // UUID version 5-ish
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    private static WirelessAccessPoint ReadAccessPoint(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Site = reader.GetString(2),
        Band = reader.GetString(3),
        Channel = reader.GetInt32(4),
        Clients = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        UtilizationPercent = ReadDouble(reader, 6),
        NoiseFloorDbm = ReadDouble(reader, 7),
        SnrDb = ReadDouble(reader, 8),
        Firmware = reader.GetString(9),
        Controller = reader.GetString(10),
        Status = (HealthStatus)reader.GetInt32(11),
        LastSeen = ReadDate(reader, 12) ?? DateTimeOffset.MinValue,
        Notes = reader.GetString(13)
    };
}
