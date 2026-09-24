using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrgMonitor.Api.Models;

namespace OrgMonitor.Api.Services;

/// <summary>
/// Delivers alert webhooks to explicitly configured channels. Delivery outcomes are
/// recorded so operators can verify whether notifications actually arrived.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Channel<MonitorAlert> _queue = Channel.CreateBounded<MonitorAlert>(
        new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly SqliteMonitoringStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(SqliteMonitoringStore store, IHttpClientFactory httpClientFactory, ILogger<NotificationDispatcher> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void Enqueue(MonitorAlert alert) => _queue.Writer.TryWrite(alert);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await DeliverAsync(alert, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Webhook delivery failed for alert {AlertId}", alert.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // host shutdown
        }
    }

    private async Task DeliverAsync(MonitorAlert alert, CancellationToken cancellationToken)
    {
        var channels = _store.GetNotificationChannels()
            .Where(channel => channel.Enabled &&
                              channel.Type == NotificationChannelType.webhook &&
                              channel.MinSeverity <= alert.Severity);

        var payload = JsonSerializer.Serialize(new
        {
            @event = "alert.raised",
            alert = new
            {
                id = alert.Id,
                severity = alert.Severity,
                title = alert.Title,
                message = alert.Message,
                entityName = alert.EntityName,
                deviceId = alert.DeviceId,
                targetId = alert.TargetId,
                ruleId = alert.RuleId,
                createdAt = alert.CreatedAt
            }
        }, JsonOptions);

        foreach (var channel in channels)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("webhook");
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(channel.Url, content, cancellationToken);
                var success = response.IsSuccessStatusCode;
                _store.RecordNotificationDelivery(
                    channel.Id,
                    alert.Id,
                    success,
                    (int)response.StatusCode,
                    success ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                if (!success)
                {
                    _logger.LogWarning("Webhook {Channel} returned HTTP {Status}", channel.Name, (int)response.StatusCode);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Webhook {Channel} delivery failed", channel.Name);
                _store.RecordNotificationDelivery(channel.Id, alert.Id, false, null, Truncate(exception.Message));
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];
}
