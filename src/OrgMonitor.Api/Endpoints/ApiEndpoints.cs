using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OrgMonitor.Api.Models;
using OrgMonitor.Api.Services;
using OrgMonitor.Api.Services.Auth;
using OrgMonitor.Api.Services.Events;

namespace OrgMonitor.Api.Endpoints;

/// <summary>
/// Complete HTTP surface. Access rules:
/// viewer = read-only dashboard, operator = day-to-day mutations,
/// admin = accounts/audit/backup/retention, agent key = heartbeats + wireless ingest.
/// </summary>
public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static void MapApiEndpoints(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<SqliteMonitoringStore>();
        var access = app.Services.GetRequiredService<AccessPolicy>();
        var auth = app.Services.GetRequiredService<AuthService>();
        var alertEngine = app.Services.GetRequiredService<AlertEngine>();
        var events = app.Services.GetRequiredService<MonitoringEventBus>();
        var dispatcher = app.Services.GetRequiredService<NotificationDispatcher>();
        var configuration = app.Configuration;
        var environment = app.Environment;

        auth.Bootstrap();

        void Audit(HttpContext context, string action, string entityType, string entityId, string details)
        {
            var principal = access.Resolve(context);
            store.AppendAudit(
                action,
                principal?.Actor ?? "anonymous",
                principal?.UserId,
                entityType,
                entityId,
                details,
                AccessPolicy.ClientAddress(context));
        }

        // ---- health ----------------------------------------------------------

        app.MapGet("/health", () =>
        {
            if (!store.IsHealthy())
            {
                return Results.Json(new { status = "degraded", database = "sqlite" }, statusCode: 503);
            }

            return Results.Ok(new
            {
                status = "ok",
                database = "sqlite",
                schemaVersion = store.SchemaVersion,
                generatedAt = DateTimeOffset.UtcNow
            });
        });

        // ---- authentication --------------------------------------------------

        app.MapPost("/api/auth/login", (HttpContext context, LoginRequest? request) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "A login body is required." });
            }

            var (user, error) = auth.Login(context, request.Username, request.Password);
            return user is null
                ? Results.BadRequest(new { error })
                : Results.Ok(new { user });
        });

        app.MapPost("/api/auth/logout", (HttpContext context) =>
        {
            auth.Logout(context, access.Resolve(context));
            return Results.Ok(new { signedOut = true });
        });

        app.MapGet("/api/auth/me", (HttpContext context) =>
        {
            var principal = access.Resolve(context);
            if (principal is null)
            {
                return Results.Ok(new { user = (UserAccount?)null, actor = (string?)null, role = (UserRole?)null });
            }

            var user = principal.UserId is { } id ? store.GetUser(id) : null;
            return Results.Ok(new { user, actor = principal.Actor, role = (UserRole?)principal.Role });
        });

        app.MapPost("/api/auth/password", (HttpContext context, ChangePasswordRequest? request) =>
        {
            if (access.Resolve(context) is not { } principal)
            {
                return Results.Unauthorized();
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A password body is required." });
            }

            var error = auth.ChangePassword(context, principal, request.CurrentPassword, request.NewPassword);
            return error is null
                ? Results.Ok(new { changed = true })
                : Results.BadRequest(new { error });
        });

        app.MapPost("/api/auth/recovery/request", (HttpContext context, PasswordRecoveryRequest? request) =>
        {
            auth.RequestRecovery(context, request?.Username);
            // Always the same answer: reveals nothing about whether the account exists.
            return Results.Ok(new { requested = true });
        });

        app.MapPost("/api/auth/recovery/confirm", (HttpContext context, PasswordRecoveryConfirm? request) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "A recovery body is required." });
            }

            var error = auth.ConfirmRecovery(context, request.Token, request.NewPassword);
            return error is null
                ? Results.Ok(new { reset = true })
                : Results.BadRequest(new { error });
        });

        // ---- overview / system ----------------------------------------------

        app.MapGet("/api/overview", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetOverview());
        });

        app.MapGet("/api/system", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            var process = Process.GetCurrentProcess();
            var startedAt = process.StartTime.ToUniversalTime();
            return Results.Ok(new SystemInfo(
                typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                environment.EnvironmentName,
                startedAt,
                Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 1),
                store.GetDatabaseSizeBytes(),
                store.SchemaVersion,
                store.RetentionDays));
        });

        // ---- managed devices -------------------------------------------------

        app.MapGet("/api/devices", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetDevices());
        });

        app.MapGet("/api/devices/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            var device = store.GetDevice(id);
            return device is null ? Results.NotFound(new { error = "Device not found." }) : Results.Ok(device);
        });

        app.MapGet("/api/devices/{id:guid}/metrics", (HttpContext context, Guid id, int? hours) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            if (store.GetDevice(id) is null)
            {
                return Results.NotFound(new { error = "Device not found." });
            }

            var window = Math.Clamp(hours ?? 24, 1, 720);
            var points = store.GetDeviceMetrics(id, DateTimeOffset.UtcNow.AddHours(-window));
            return Results.Ok(new { hours = window, points });
        });

        app.MapPut("/api/devices/{id:guid}", (HttpContext context, Guid id, UpdateDeviceRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "An update body is required." });
            }

            if (request.Name is not null && (request.Name.Trim().Length == 0 || request.Name.Trim().Length > 120))
            {
                return Results.BadRequest(new { error = "name must be between 1 and 120 characters." });
            }

            if (request.Tags is { Length: > 500 })
            {
                return Results.BadRequest(new { error = "tags must be 500 characters or fewer." });
            }

            if (request.Kind is { } kind && !Enum.IsDefined(typeof(DeviceKind), kind))
            {
                return Results.BadRequest(new { error = "kind is not a supported device type." });
            }

            var device = store.UpdateDevice(id, request);
            if (device is null)
            {
                return Results.NotFound(new { error = "Device not found." });
            }

            Audit(context, "device.updated", "device", id.ToString(), $"Updated device {device.Name}");
            events.Publish("device.updated", id);
            return Results.Ok(device);
        });

        app.MapDelete("/api/devices/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            var device = store.GetDevice(id);
            if (device is null || !store.DeleteDevice(id))
            {
                return Results.NotFound(new { error = "Device not found." });
            }

            Audit(context, "device.deleted", "device", id.ToString(), $"Deleted device {device.Name}");
            events.Publish("device.deleted", id);
            return Results.Ok(new { deleted = true });
        });

        app.MapPost("/api/agents/heartbeat", (HttpContext context, AgentHeartbeat? heartbeat) =>
        {
            if (ApiKeyGuard.Validate(context, configuration, environment, "Monitoring:AgentApiKey", "X-Agent-Key") is { } keyError)
            {
                return keyError;
            }

            if (RequestValidation.ValidateHeartbeat(heartbeat) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var previous = store.GetDevice(heartbeat!.DeviceId);
            var device = store.UpsertDevice(heartbeat);
            if (previous?.Status == HealthStatus.Offline)
            {
                alertEngine.OnDeviceRecovered(device.Id, device.Name);
            }

            alertEngine.OnDeviceMetrics(device);
            events.Publish("device.updated", device.Id);
            return Results.Ok(device);
        });

        // ---- network targets -------------------------------------------------

        app.MapGet("/api/network", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetNetworkTargets());
        });

        app.MapGet("/api/network/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            var target = store.GetNetworkTarget(id);
            return target is null ? Results.NotFound(new { error = "Target not found." }) : Results.Ok(target);
        });

        app.MapGet("/api/network/{id:guid}/history", (HttpContext context, Guid id, int? hours) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            if (store.GetNetworkTarget(id) is null)
            {
                return Results.NotFound(new { error = "Target not found." });
            }

            var window = Math.Clamp(hours ?? 24, 1, 720);
            var since = DateTimeOffset.UtcNow.AddHours(-window);
            return Results.Ok(new
            {
                hours = window,
                points = store.GetProbeHistory(id, since),
                availability = store.GetTargetAvailability(id, since)
            });
        });

        app.MapPost("/api/network/targets", (HttpContext context, CreateNetworkTargetRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A target body is required." });
            }

            if (RequestValidation.ValidateTarget(
                    request.Name ?? "New target",
                    request.Address,
                    request.Port,
                    request.Protocol,
                    request.Kind,
                    request.IntervalSeconds) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var target = store.AddNetworkTarget(request);
            Audit(context, "target.created", "target", target.Id.ToString(), $"Created target {target.Name}");
            events.Publish("target.created", target.Id);
            return Results.Created($"/api/network/{target.Id}", target);
        });

        app.MapPut("/api/network/targets/{id:guid}", (HttpContext context, Guid id, UpdateNetworkTargetRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "An update body is required." });
            }

            var existing = store.GetNetworkTarget(id);
            if (existing is null)
            {
                return Results.NotFound(new { error = "Target not found." });
            }

            if (RequestValidation.ValidateTarget(
                    request.Name ?? existing.Name,
                    request.Address ?? existing.Address,
                    request.Port ?? existing.Port,
                    request.Protocol ?? existing.Protocol,
                    request.Kind ?? existing.Kind,
                    request.IntervalSeconds ?? existing.IntervalSeconds) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var target = store.UpdateNetworkTarget(id, request);
            if (target is null)
            {
                return Results.NotFound(new { error = "Target not found." });
            }

            Audit(context, "target.updated", "target", id.ToString(), $"Updated target {target.Name}");
            events.Publish("target.updated", id);
            return Results.Ok(target);
        });

        app.MapDelete("/api/network/targets/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            var target = store.GetNetworkTarget(id);
            if (target is null || !store.DeleteNetworkTarget(id))
            {
                return Results.NotFound(new { error = "Target not found." });
            }

            Audit(context, "target.deleted", "target", id.ToString(), $"Deleted target {target.Name}");
            events.Publish("target.deleted", id);
            return Results.Ok(new { deleted = true });
        });

        // ---- alerts ----------------------------------------------------------

        app.MapGet("/api/alerts", (HttpContext context, bool? includeAcknowledged) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetAlerts(includeAcknowledged ?? false));
        });

        app.MapGet("/api/alerts/{alertId:guid}", (HttpContext context, Guid alertId) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            var alert = store.GetAlert(alertId);
            return alert is null ? Results.NotFound(new { error = "Alert not found." }) : Results.Ok(alert);
        });

        app.MapPost("/api/alerts/{alertId:guid}/acknowledge", (HttpContext context, Guid alertId) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            var actor = access.Resolve(context)?.Actor ?? "unknown";
            if (!store.AcknowledgeAlert(alertId, actor))
            {
                return Results.NotFound(new { error = "Alert not found or already acknowledged." });
            }

            Audit(context, "alert.acknowledged", "alert", alertId.ToString(), "Acknowledged alert");
            events.Publish("alert.updated", alertId);
            return Results.Ok(new { acknowledged = true });
        });

        // ---- alert rules -----------------------------------------------------

        app.MapGet("/api/alerts/rules", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetAlertRules());
        });

        app.MapPost("/api/alerts/rules", (HttpContext context, CreateAlertRuleRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (ValidateRule(request) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var rule = store.AddAlertRule(request!);
            Audit(context, "rule.created", "rule", rule.Id.ToString(), $"Created rule {rule.Name}");
            events.Publish("rule.changed", rule.Id);
            return Results.Created($"/api/alerts/rules/{rule.Id}", rule);
        });

        app.MapPut("/api/alerts/rules/{id:guid}", (HttpContext context, Guid id, UpdateAlertRuleRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "An update body is required." });
            }

            if (request.Threshold is { } threshold && !double.IsFinite(threshold))
            {
                return Results.BadRequest(new { error = "threshold must be a finite number." });
            }

            if (request.ConsecutiveBreaches is < 1 or > 20)
            {
                return Results.BadRequest(new { error = "consecutiveBreaches must be between 1 and 20." });
            }

            var rule = store.UpdateAlertRule(id, request);
            if (rule is null)
            {
                return Results.NotFound(new { error = "Rule not found." });
            }

            Audit(context, "rule.updated", "rule", id.ToString(), $"Updated rule {rule.Name}");
            events.Publish("rule.changed", id);
            return Results.Ok(rule);
        });

        app.MapDelete("/api/alerts/rules/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (!store.DeleteAlertRule(id))
            {
                return Results.NotFound(new { error = "Rule not found." });
            }

            Audit(context, "rule.deleted", "rule", id.ToString(), "Deleted alert rule");
            events.Publish("rule.changed", id);
            return Results.Ok(new { deleted = true });
        });

        // ---- maintenance windows --------------------------------------------

        app.MapGet("/api/maintenance", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetMaintenanceWindows());
        });

        app.MapPost("/api/maintenance", (HttpContext context, CreateMaintenanceWindowRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A maintenance body is required." });
            }

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            {
                return Results.BadRequest(new { error = "A name of at most 120 characters is required." });
            }

            if (!Enum.IsDefined(typeof(MaintenanceScope), request.Scope))
            {
                return Results.BadRequest(new { error = "scope must be all, device, or target." });
            }

            if (request.Scope != MaintenanceScope.all && request.ScopeId is null)
            {
                return Results.BadRequest(new { error = "scopeId is required for a device or target scope." });
            }

            if (request.EndsAt <= request.StartsAt)
            {
                return Results.BadRequest(new { error = "endsAt must be after startsAt." });
            }

            if (request.EndsAt - request.StartsAt > TimeSpan.FromDays(30))
            {
                return Results.BadRequest(new { error = "A maintenance window may not exceed 30 days." });
            }

            var principal = access.Resolve(context);
            var window = store.AddMaintenanceWindow(request, principal?.Actor ?? "unknown");
            Audit(context, "maintenance.created", "maintenance", window.Id.ToString(),
                $"Scheduled maintenance {window.Name}");
            events.Publish("maintenance.changed", window.Id);
            return Results.Created($"/api/maintenance/{window.Id}", window);
        });

        app.MapDelete("/api/maintenance/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (!store.DeleteMaintenanceWindow(id))
            {
                return Results.NotFound(new { error = "Maintenance window not found." });
            }

            Audit(context, "maintenance.deleted", "maintenance", id.ToString(), "Deleted maintenance window");
            events.Publish("maintenance.changed", id);
            return Results.Ok(new { deleted = true });
        });

        // ---- wireless --------------------------------------------------------

        app.MapGet("/api/wireless", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(new
            {
                summary = store.GetWirelessSummary(),
                accessPoints = store.GetAccessPoints()
            });
        });

        app.MapGet("/api/wireless/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            var accessPoint = store.GetAccessPoint(id);
            return accessPoint is null
                ? Results.NotFound(new { error = "Access point not found." })
                : Results.Ok(accessPoint);
        });

        app.MapGet("/api/wireless/{id:guid}/history", (HttpContext context, Guid id, int? hours) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            if (store.GetAccessPoint(id) is null)
            {
                return Results.NotFound(new { error = "Access point not found." });
            }

            var window = Math.Clamp(hours ?? 24, 1, 720);
            var points = store.GetAccessPointHistory(id, DateTimeOffset.UtcNow.AddHours(-window));
            return Results.Ok(new { hours = window, points });
        });

        app.MapPost("/api/wireless/ingest", (HttpContext context, WirelessIngestRequest? request) =>
        {
            // Accept either an operator/admin session or the agent key used by collectors.
            var denied = access.Require(context, UserRole.@operator);
            if (denied is not null &&
                ApiKeyGuard.Validate(context, configuration, environment, "Monitoring:AgentApiKey", "X-Agent-Key") is not null)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A snapshot body is required." });
            }

            if (request.AccessPoints is null || request.AccessPoints.Count == 0)
            {
                return Results.BadRequest(new { error = "accessPoints must contain at least one access point." });
            }

            if (request.AccessPoints.Count > 2000)
            {
                return Results.BadRequest(new { error = "A snapshot may contain at most 2000 access points." });
            }

            for (var index = 0; index < request.AccessPoints.Count; index++)
            {
                var accessPoint = request.AccessPoints[index];
                if (string.IsNullOrWhiteSpace(accessPoint.Name) && string.IsNullOrWhiteSpace(accessPoint.Id))
                {
                    return Results.BadRequest(new { error = $"accessPoints[{index}] requires a name or id." });
                }

                if (accessPoint.UtilizationPercent is { } utilization &&
                    (!double.IsFinite(utilization) || utilization is < 0 or > 100))
                {
                    return Results.BadRequest(new { error = $"accessPoints[{index}].utilizationPercent must be from 0 to 100." });
                }

                if (accessPoint.Clients is < 0)
                {
                    return Results.BadRequest(new { error = $"accessPoints[{index}].clients must not be negative." });
                }
            }

            var capturedAt = request.CapturedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
            var result = store.UpsertAccessPoints(request.AccessPoints, request.Controller, capturedAt);

            foreach (var accessPoint in request.AccessPoints)
            {
                if (accessPoint.Status is HealthStatus.Online or HealthStatus.Warning)
                {
                    var id = store.GetAccessPoints()
                        .FirstOrDefault(ap => ap.Name == accessPoint.Name && ap.Site == (accessPoint.Site ?? string.Empty))?.Id;
                    if (id is not null)
                    {
                        alertEngine.OnAccessPointRecovered(id.Value);
                    }
                }
            }

            Audit(context, "wireless.ingested", "wireless", request.Controller ?? "controller",
                $"Ingested {result.Received} access points");
            events.Publish("wireless.updated");
            return Results.Ok(result);
        });

        // ---- notification channels ------------------------------------------

        app.MapGet("/api/notifications/channels", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetNotificationChannels());
        });

        app.MapPost("/api/notifications/channels", (HttpContext context, CreateNotificationChannelRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A channel body is required." });
            }

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            {
                return Results.BadRequest(new { error = "A name of at most 120 characters is required." });
            }

            if (RequestValidation.ValidateWebhookUrl(request.Url) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (!Enum.IsDefined(typeof(AlertSeverity), request.MinSeverity))
            {
                return Results.BadRequest(new { error = "minSeverity must be info, warning, or critical." });
            }

            var channel = store.AddNotificationChannel(request);
            Audit(context, "channel.created", "channel", channel.Id.ToString(), $"Created webhook {channel.Name}");
            events.Publish("channel.changed", channel.Id);
            return Results.Created($"/api/notifications/channels/{channel.Id}", channel);
        });

        app.MapPut("/api/notifications/channels/{id:guid}", (HttpContext context, Guid id, UpdateNotificationChannelRequest? request) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "An update body is required." });
            }

            if (request.Url is not null && RequestValidation.ValidateWebhookUrl(request.Url) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var channel = store.UpdateNotificationChannel(id, request);
            if (channel is null)
            {
                return Results.NotFound(new { error = "Channel not found." });
            }

            Audit(context, "channel.updated", "channel", id.ToString(), $"Updated webhook {channel.Name}");
            events.Publish("channel.changed", id);
            return Results.Ok(channel);
        });

        app.MapDelete("/api/notifications/channels/{id:guid}", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            if (!store.DeleteNotificationChannel(id))
            {
                return Results.NotFound(new { error = "Channel not found." });
            }

            Audit(context, "channel.deleted", "channel", id.ToString(), "Deleted webhook channel");
            events.Publish("channel.changed", id);
            return Results.Ok(new { deleted = true });
        });

        app.MapPost("/api/notifications/channels/{id:guid}/test", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.@operator) is { } denied)
            {
                return denied;
            }

            var channel = store.GetNotificationChannels().FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                return Results.NotFound(new { error = "Channel not found." });
            }

            dispatcher.Enqueue(new MonitorAlert
            {
                Id = Guid.NewGuid(),
                Severity = AlertSeverity.info,
                Title = "OrgMonitor test notification",
                Message = $"Test delivery from channel '{channel.Name}'.",
                EntityName = channel.Name,
                CreatedAt = DateTimeOffset.UtcNow
            });

            Audit(context, "channel.tested", "channel", id.ToString(), $"Queued test for {channel.Name}");
            return Results.Ok(new { queued = true });
        });

        app.MapGet("/api/notifications/deliveries", (HttpContext context, int? limit) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetRecentDeliveries(limit ?? 50));
        });

        // ---- administration --------------------------------------------------

        app.MapGet("/api/admin/users", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetUsers());
        });

        app.MapPost("/api/admin/users", (HttpContext context, CreateUserRequest? request) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "A user body is required." });
            }

            if (RequestValidation.ValidateUsername(request.Username) is { } usernameError)
            {
                return Results.BadRequest(new { error = usernameError });
            }

            if (RequestValidation.ValidatePassword(request.Password, 10) is { } passwordError)
            {
                return Results.BadRequest(new { error = passwordError });
            }

            if (RequestValidation.ValidateEmail(request.Email) is { } emailError)
            {
                return Results.BadRequest(new { error = emailError });
            }

            if (!Enum.IsDefined(typeof(UserRole), request.Role))
            {
                return Results.BadRequest(new { error = "role must be admin, operator, or viewer." });
            }

            if (store.CreateUser(request.Username!, request.Email, PasswordHasher.Hash(request.Password!), request.Role) is { } createError)
            {
                return Results.Conflict(new { error = createError });
            }

            var user = store.GetCredentialByUsername(request.Username!) is { } credential
                ? store.GetUser(credential.Id)
                : null;

            Audit(context, "user.created", "user", user?.Id.ToString() ?? string.Empty,
                $"Created user {request.Username} with role {request.Role}");
            return Results.Created("/api/admin/users", user);
        });

        app.MapPut("/api/admin/users/{id:guid}", (HttpContext context, Guid id, UpdateUserRequest? request) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "An update body is required." });
            }

            if (request.Role is { } role && !Enum.IsDefined(typeof(UserRole), role))
            {
                return Results.BadRequest(new { error = "role must be admin, operator, or viewer." });
            }

            if (RequestValidation.ValidateEmail(request.Email) is { } emailError)
            {
                return Results.BadRequest(new { error = emailError });
            }

            var principal = access.Resolve(context);
            if (principal?.UserId == id &&
                ((request.Active == false) || (request.Role is { } newRole && newRole != UserRole.admin)))
            {
                return Results.BadRequest(new { error = "You cannot demote or disable your own account." });
            }

            var user = store.UpdateUser(id, request.Role, request.Active, request.Email);
            if (user is null)
            {
                return Results.NotFound(new { error = "User not found." });
            }

            Audit(context, "user.updated", "user", id.ToString(),
                $"Updated user {user.Username} (role {user.Role}, active {user.Active})");
            return Results.Ok(user);
        });

        app.MapPost("/api/admin/users/{id:guid}/password", (HttpContext context, Guid id, ChangePasswordRequest? request) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            var user = store.GetUser(id);
            if (user is null)
            {
                return Results.NotFound(new { error = "User not found." });
            }

            if (RequestValidation.ValidatePassword(request?.NewPassword, 10) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            store.SetPasswordHash(id, PasswordHasher.Hash(request!.NewPassword!));
            // Suspected-compromise response: every live session dies and the user must rotate again.
            var revoked = store.RevokeAllSessions(id);
            store.SetMustChangePassword(id, true);
            Audit(context, "user.password_reset", "user", id.ToString(),
                $"Reset password for {user.Username}; revoked {revoked} session(s)");
            return Results.Ok(new { changed = true, sessionsRevoked = revoked, mustChangePassword = true });
        });

        app.MapPost("/api/admin/users/{id:guid}/reset-token", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            var (token, expiresAt, error) = auth.IssueResetToken(context, access.Resolve(context)!, id);
            return error is not null
                ? Results.NotFound(new { error })
                : Results.Ok(new
                {
                    token,
                    expiresAt,
                    url = auth.BuildResetLink(context, token!)
                });
        });

        app.MapPost("/api/admin/users/{id:guid}/revoke-sessions", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            if (store.GetUser(id) is null)
            {
                return Results.NotFound(new { error = "User not found." });
            }

            var revoked = auth.RevokeSessions(context, access.Resolve(context)!, id);
            return Results.Ok(new { revoked });
        });

        app.MapPost("/api/admin/users/{id:guid}/unlock", (HttpContext context, Guid id) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            var user = store.GetUser(id);
            if (user is null)
            {
                return Results.NotFound(new { error = "User not found." });
            }

            if (!store.UnlockUser(id))
            {
                return Results.NotFound(new { error = "User not found." });
            }

            Audit(context, "user.unlocked", "user", id.ToString(), $"Unlocked account {user.Username}");
            return Results.Ok(new { unlocked = true });
        });

        app.MapGet("/api/admin/audit", (HttpContext context, int? limit) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            return Results.Ok(store.GetAuditEntries(limit ?? 100));
        });

        app.MapPost("/api/admin/backup", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            try
            {
                var path = store.CreateBackup();
                Audit(context, "database.backed_up", "database", path, "Created SQLite backup");
                return Results.Ok(new { path });
            }
            catch (Exception exception)
            {
                return Results.Problem($"Backup failed: {exception.Message}");
            }
        });

        app.MapPost("/api/admin/retention/prune", (HttpContext context) =>
        {
            if (access.Require(context, UserRole.admin) is { } denied)
            {
                return denied;
            }

            var pruned = store.PruneRetention();
            Audit(context, "retention.pruned", "database", string.Empty, $"Pruned {pruned} history rows");
            return Results.Ok(new
            {
                metrics = pruned.Metrics,
                probes = pruned.Probes,
                wirelessHistory = pruned.WirelessHistory,
                auditEntries = pruned.Audit
            });
        });

        // ---- server-sent events ---------------------------------------------

        app.MapGet("/api/events", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (access.Require(context, UserRole.viewer) is { } denied)
            {
                return denied;
            }

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            using var subscription = events.Subscribe();
            var reader = subscription.Reader;
            Task<MonitoringEvent?>? pending = null;

            try
            {
                while (true)
                {
                    pending ??= events.ReadNextAsync(reader, cancellationToken).AsTask();
                    var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(15)));

                    if (completed != pending)
                    {
                        await context.Response.WriteAsync(": ping\n\n", cancellationToken);
                        continue;
                    }

                    var monitoringEvent = await pending;
                    pending = null;
                    if (monitoringEvent is null)
                    {
                        break;
                    }

                    var payload = JsonSerializer.Serialize(
                        new { type = monitoringEvent.Type, entityId = monitoringEvent.EntityId, occurredAt = monitoringEvent.OccurredAt },
                        EventJson);
                    await context.Response.WriteAsync(
                        $"event: {monitoringEvent.Type}\ndata: {payload}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ConnectionAbortedException or IOException)
            {
                // browser disconnected
            }
            finally
            {
                if (pending is not null)
                {
                    _ = pending.ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }

            return Results.Empty;
        });

        // ---- static configuration helpers -----------------------------------

        static string? ValidateRule(CreateAlertRuleRequest? request)
        {
            if (request is null)
            {
                return "A rule body is required.";
            }

            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            {
                return "A rule name of at most 120 characters is required.";
            }

            if (!double.IsFinite(request.Threshold))
            {
                return "threshold must be a finite number.";
            }

            if (!Enum.IsDefined(typeof(AlertMetric), request.Metric))
            {
                return "metric is not supported.";
            }

            if (!Enum.IsDefined(typeof(AlertComparator), request.Comparator))
            {
                return "comparator is not supported.";
            }

            if (!Enum.IsDefined(typeof(AlertSeverity), request.Severity))
            {
                return "severity must be info, warning, or critical.";
            }

            if (request.ScopeKind is { } scope && !Enum.IsDefined(typeof(DeviceKind), scope))
            {
                return "scopeKind is not a supported device type.";
            }

            if (request.ConsecutiveBreaches is < 1 or > 20)
            {
                return "consecutiveBreaches must be between 1 and 20.";
            }

            return null;
        }
    }
}
