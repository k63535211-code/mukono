using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrgMonitor.Agent;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task Main()
    {
        var server = Environment.GetEnvironmentVariable("ORGMONITOR_SERVER")?.TrimEnd('/') ?? "http://localhost:5080";
        var apiKey = Environment.GetEnvironmentVariable("ORGMONITOR_API_KEY");
        var intervalSeconds = ParseInterval(Environment.GetEnvironmentVariable("ORGMONITOR_INTERVAL_SECONDS"));
        var deviceId = DeviceIdentity.LoadOrCreate();
        var name = Environment.GetEnvironmentVariable("ORGMONITOR_NAME") ?? Environment.MachineName;
        var address = Environment.GetEnvironmentVariable("ORGMONITOR_ADDRESS") ?? GetLocalAddress();
        var tags = Environment.GetEnvironmentVariable("ORGMONITOR_TAGS") ?? string.Empty;
        var kind = ParseKind(Environment.GetEnvironmentVariable("ORGMONITOR_KIND"));
        var metrics = new SystemMetrics();
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"{server}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(10)
        };
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        Console.WriteLine($"OrgMonitor agent starting for {name} ({deviceId})");
        Console.WriteLine($"Server: {server}; interval: {intervalSeconds}s");

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var snapshot = metrics.Read();
                var heartbeat = new AgentHeartbeat
                {
                    DeviceId = deviceId,
                    Name = name,
                    Hostname = Environment.MachineName,
                    Address = address,
                    Kind = kind,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    AgentVersion = "0.1.0",
                    CpuPercent = snapshot.CpuPercent,
                    MemoryPercent = snapshot.MemoryPercent,
                    DiskPercent = snapshot.DiskPercent,
                    Tags = tags
                };
                await SendHeartbeatAsync(client, apiKey, heartbeat, stopping.Token);
                Console.WriteLine($"Heartbeat sent at {DateTimeOffset.UtcNow:O}");
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Heartbeat failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
        }

        Console.WriteLine("OrgMonitor agent stopped");
    }

    private static async Task SendHeartbeatAsync(HttpClient client, string? apiKey, AgentHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/agents/heartbeat")
        {
            Content = JsonContent.Create(heartbeat, options: JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Agent-Key", apiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Agent API returned {(int)response.StatusCode}: {body}");
        }
    }

    private static int ParseInterval(string? value)
    {
        return int.TryParse(value, out var seconds) ? Math.Clamp(seconds, 10, 3600) : 60;
    }

    private static DeviceKind ParseKind(string? value)
    {
        return Enum.TryParse<DeviceKind>(value, true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : DeviceKind.Workstation;
    }

    private static string GetLocalAddress()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

internal sealed class AgentHeartbeat
{
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public DeviceKind Kind { get; init; }
    public string OperatingSystem { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = string.Empty;
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
    public string Tags { get; init; } = string.Empty;
}

internal sealed class MetricsSnapshot
{
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskPercent { get; init; }
}

internal sealed class SystemMetrics
{
    private long? _previousIdle;
    private long? _previousTotal;

    public MetricsSnapshot Read()
    {
        return new MetricsSnapshot
        {
            CpuPercent = ReadSafely(ReadCpuPercent),
            MemoryPercent = ReadSafely(ReadMemoryPercent),
            DiskPercent = ReadSafely(ReadDiskPercent)
        };
    }

    private static double? ReadSafely(Func<double?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private double? ReadCpuPercent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(value => long.TryParse(value, out var parsed) ? parsed : 0L)
                .ToArray();
            if (values.Length < 5)
            {
                return null;
            }

            var idle = values[3] + values[4];
            var total = values.Sum();
            if (_previousTotal is null || _previousIdle is null)
            {
                _previousIdle = idle;
                _previousTotal = total;
                return null;
            }

            var totalDelta = total - _previousTotal.Value;
            var idleDelta = idle - _previousIdle.Value;
            _previousIdle = idle;
            _previousTotal = total;
            return totalDelta <= 0 ? null : Math.Clamp((1 - (double)idleDelta / totalDelta) * 100, 0, 100);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private double? ReadMemoryPercent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            long? total = null;
            long? available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKilobytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKilobytes(line);
                }

                if (total is not null && available is not null)
                {
                    break;
                }
            }

            return total is > 0 && available is not null
                ? Math.Clamp((1 - (double)available.Value / total.Value) * 100, 0, 100)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private double? ReadDiskPercent()
    {
        try
        {
            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(candidate => candidate.IsReady && candidate.TotalSize > 0);
            return drive is null ? null : Math.Clamp((1 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100, 0, 100);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ParseKilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value : null;
    }
}

internal enum DeviceKind
{
    Workstation,
    Server,
    Router,
    Switch,
    AccessPoint,
    Firewall,
    Printer,
    Other
}

internal static class DeviceIdentity
{
    public static Guid LoadOrCreate()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("ORGMONITOR_STATE_DIR");
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : configuredDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("A writable agent state directory could not be determined.");
        }

        directory = Path.Combine(directory, "OrgMonitor");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-id");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyPath = string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".orgmonitor", "device-id");
        if (!File.Exists(path) && legacyPath is not null && TryRead(legacyPath, out var legacyId))
        {
            try
            {
                File.Copy(legacyPath, path, overwrite: false);
            }
            catch (IOException)
            {
                // The legacy ID is still usable when migration cannot be written.
            }

            return legacyId;
        }

        if (TryRead(path, out var existing))
        {
            return existing;
        }

        var created = Guid.NewGuid();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(created.ToString());
            return created;
        }
        catch (IOException)
        {
            if (TryReadWithRetry(path, out var raced))
            {
                return raced;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                throw new IOException($"The agent identity file could not be initialized: {path}");
            }

            return LoadOrCreate();
        }
    }

    private static bool TryReadWithRetry(string path, out Guid deviceId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (TryRead(path, out deviceId))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        deviceId = Guid.Empty;
        return false;
    }

    private static bool TryRead(string path, out Guid deviceId)
    {
        try
        {
            return Guid.TryParse(File.ReadAllText(path).Trim(), out deviceId);
        }
        catch (IOException)
        {
            deviceId = Guid.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            deviceId = Guid.Empty;
            return false;
        }
    }
}
