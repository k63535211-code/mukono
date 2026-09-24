using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>
/// Background loop: stale-device/AP detection, per-target interval probing,
/// history retention pruning, and expired-session cleanup.
/// </summary>
public sealed class MonitoringWorker : BackgroundService
{
    private const int PruneEveryTicks = 120;

    private readonly SqliteMonitoringStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly AlertEngine _alertEngine;
    private readonly ILogger<MonitoringWorker> _logger;

    public MonitoringWorker(
        SqliteMonitoringStore store,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        AlertEngine alertEngine,
        ILogger<MonitoringWorker> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _alertEngine = alertEngine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Clamp(_configuration.GetValue("Monitoring:ProbeIntervalSeconds", 30), 5, 3600);
        var offlineAfter = TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("Monitoring:OfflineAfterMinutes", 5), 1, 1440));
        var accessPointStaleAfter = TimeSpan.FromMinutes(
            Math.Clamp(_configuration.GetValue("Monitoring:AccessPointStaleMinutes", 15), 1, 1440));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        var ticks = 0;

        try
        {
            do
            {
                ticks++;
                try
                {
                    foreach (var device in _store.MarkStaleDevicesOffline(offlineAfter))
                    {
                        _alertEngine.OnDeviceOffline(device.Id, device.Name);
                    }

                    foreach (var accessPoint in _store.MarkStaleAccessPointsOffline(accessPointStaleAfter))
                    {
                        _alertEngine.OnAccessPointOffline(accessPoint.Id, accessPoint.Name);
                    }

                    await ProbeTargetsAsync(stoppingToken);

                    if (ticks % PruneEveryTicks == 1)
                    {
                        var pruned = _store.PruneRetention();
                        var sessions = _store.PruneExpiredSessions();
                        if (pruned != default || sessions > 0)
                        {
                            _logger.LogInformation(
                                "Retention prune: {Metrics} metrics, {Probes} probes, {Wireless} wireless, {Audit} audit, {Sessions} sessions",
                                pruned.Metrics, pruned.Probes, pruned.WirelessHistory, pruned.Audit, sessions);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Monitoring cycle failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // host shutdown
        }
    }

    private async Task ProbeTargetsAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var due = _store.GetNetworkTargets()
            .Where(target => target.Enabled)
            .Where(target => target.LastChecked is null ||
                             target.LastChecked.Value.AddSeconds(Math.Clamp(target.IntervalSeconds, 5, 86_400)) <= now)
            .ToArray();

        if (due.Length == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4);
        var probes = due.Select(async target =>
        {
            await gate.WaitAsync(stoppingToken);
            try
            {
                await ProbeTargetAsync(target, stoppingToken);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(probes);
    }

    private static string BuildHttpProbeAddress(NetworkTarget target)
    {
        if (Uri.TryCreate(target.Address, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            return new UriBuilder(parsed)
            {
                Scheme = target.Protocol,
                Port = target.Port,
                UserName = string.Empty,
                Password = string.Empty
            }.Uri.ToString();
        }

        return new UriBuilder(target.Protocol, target.Address, target.Port).Uri.ToString();
    }

    private async Task ProbeTargetAsync(NetworkTarget target, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var status = HealthStatus.Offline;
        string? error = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            if (target.Protocol is "http" or "https")
            {
                using var client = _httpClientFactory.CreateClient("monitoring");
                using var response = await client.GetAsync(BuildHttpProbeAddress(target), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    error = $"HTTP {(int)response.StatusCode}";
                }
                else
                {
                    status = HealthStatus.Online;
                }
            }
            else
            {
                using var client = new TcpClient();
                await client.ConnectAsync(target.Address, target.Port, timeout.Token);
                status = HealthStatus.Online;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            error = "Probe timed out";
            _logger.LogDebug("Probe timed out for {Target}", target.Name);
        }
        catch (SocketException exception)
        {
            error = exception.Message;
            _logger.LogDebug(exception, "Network probe failed for {Target}", target.Name);
        }
        catch (HttpRequestException exception)
        {
            error = exception.Message;
            _logger.LogDebug(exception, "HTTP probe failed for {Target}", target.Name);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            error = exception.Message;
            _logger.LogDebug(exception, "Probe failed for {Target}", target.Name);
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var checkedAt = DateTimeOffset.UtcNow;
                    var persistence = _store.PersistProbe(
                        target.Id, status, elapsed.TotalMilliseconds, error, checkedAt);
                    _alertEngine.OnProbeResult(target, persistence, status, elapsed.TotalMilliseconds, error);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Could not persist probe result for {Target}", target.Name);
                }
            }
        }
    }
}
