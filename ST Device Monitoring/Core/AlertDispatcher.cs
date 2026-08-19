using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>
/// Sends e-mail and webhook notifications when a device goes down or recovers.
/// Everything runs in the background - the check loops are never blocked, and a failing
/// mail server can never stop the monitoring.
/// </summary>
public sealed class AlertDispatcher : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConcurrentDictionary<Guid, DateTime> _lastSent = new();

    public AlertSettings Settings { get; set; }
    public string? LastError { get; private set; }
    public long Sent { get; private set; }

    public AlertDispatcher(AlertSettings settings) => Settings = settings;

    /// <summary>Fire and forget - returns immediately.</summary>
    public void Dispatch(in DeviceAlert alert)
    {
        if (!Settings.EmailEnabled && !Settings.WebhookEnabled) return;
        if (!alert.IsDown && !Settings.NotifyOnRecovery) return;
        if (IsThrottled(alert)) return;

        var copy = alert;
        _ = Task.Run(() => SendAsync(copy));
    }

    private bool IsThrottled(in DeviceAlert alert)
    {
        var throttle = TimeSpan.FromSeconds(Math.Max(0, Settings.ThrottleSeconds));
        if (throttle <= TimeSpan.Zero) return false;

        var now = DateTime.UtcNow;
        // A "device is back" message is always allowed through.
        if (!alert.IsDown)
        {
            _lastSent[alert.Device.Id] = now;
            return false;
        }

        if (_lastSent.TryGetValue(alert.Device.Id, out var last) && now - last < throttle)
            return true;

        _lastSent[alert.Device.Id] = now;
        return false;
    }

    private async Task SendAsync(DeviceAlert alert)
    {
        var subject = alert.IsDown
            ? $"[DOWN] {alert.Device.Name} ({alert.Device.Host})"
            : $"[UP] {alert.Device.Name} ({alert.Device.Host})";

        var body = new StringBuilder()
            .AppendLine(subject)
            .AppendLine()
            .AppendLine($"Device:    {alert.Device.Name}")
            .AppendLine($"Host:      {alert.Device.Host}")
            .AppendLine($"Check:     {alert.Device.ModeText}")
            .AppendLine($"Group:     {alert.Device.Group}")
            .AppendLine($"Time:      {alert.Timestamp:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Details:   {alert.Message}")
            .AppendLine(alert.IsDown ? string.Empty : $"Downtime:  {alert.Downtime:hh\\:mm\\:ss}")
            .AppendLine()
            .AppendLine($"-- {AppInfo.ProductName} {AppInfo.VersionLine}")
            .ToString();

        if (Settings.EmailEnabled) await SendMailAsync(subject, body).ConfigureAwait(false);
        if (Settings.WebhookEnabled) await SendWebhookAsync(alert, subject).ConfigureAwait(false);
    }

    private async Task SendMailAsync(string subject, string body)
    {
        try
        {
            var recipients = Settings.MailTo
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (recipients.Length == 0 || string.IsNullOrWhiteSpace(Settings.SmtpHost)) return;

            using var message = new MailMessage
            {
                From = new MailAddress(string.IsNullOrWhiteSpace(Settings.MailFrom)
                    ? Settings.SmtpUser
                    : Settings.MailFrom),
                Subject = subject,
                Body = body
            };
            foreach (var to in recipients) message.To.Add(to);

            using var client = new SmtpClient(Settings.SmtpHost, Settings.SmtpPort)
            {
                EnableSsl = Settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 20_000
            };

            var password = DpapiProtector.Unprotect(Settings.SmtpPasswordProtected);
            if (!string.IsNullOrEmpty(Settings.SmtpUser))
                client.Credentials = new NetworkCredential(Settings.SmtpUser, password);

            await client.SendMailAsync(message).ConfigureAwait(false);
            Sent++;
        }
        catch (Exception ex)
        {
            LastError = "Mail: " + ex.GetBaseException().Message;
        }
    }

    private async Task SendWebhookAsync(DeviceAlert alert, string subject)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.WebhookUrl)) return;

            var payload = JsonSerializer.Serialize(new
            {
                text = subject + " - " + alert.Message,
                state = alert.IsDown ? "down" : "up",
                device = alert.Device.Name,
                host = alert.Device.Host,
                check = alert.Device.ModeText,
                group = alert.Device.Group,
                timestamp = alert.Timestamp.ToString("O"),
                downtimeSeconds = (long)alert.Downtime.TotalSeconds,
                message = alert.Message,
                source = AppInfo.ProductName
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Settings.WebhookUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                LastError = $"Webhook: HTTP {(int)response.StatusCode}";
            else
                Sent++;
        }
        catch (Exception ex)
        {
            LastError = "Webhook: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Used by the "Send test notification" button in the settings window.</summary>
    public async Task<string> SendTestAsync()
    {
        LastError = null;
        var alert = new DeviceAlert
        {
            Device = new DeviceConfig { Name = "Test device", Host = "127.0.0.1", Group = "Test" },
            IsDown = true,
            Timestamp = DateTime.Now,
            Message = "Test notification from ST Device Monitoring"
        };

        var before = Sent;
        await SendAsync(alert).ConfigureAwait(false);

        if (LastError != null) return "Failed: " + LastError;
        return Sent > before ? "Test notification sent." : "Nothing was sent - e-mail and webhook are both disabled.";
    }

    public void Dispose() => _lastSent.Clear();
}
