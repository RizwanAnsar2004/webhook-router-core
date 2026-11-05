using System.ComponentModel;
using System.Text.Json;
using WebhookRouter.Models.Jobs;

namespace WebhookRouter.Services;

public class HyperPayWebhookJob(
    HyperPayDecryptor _decryptor,
    WebhookRouteResolver _resolver,
    WebhookForwarder _forwarder,
    ILogger<HyperPayWebhookJob> _logger)
{
    [DisplayName("Forward HyperPay webhook")]
    public async Task ForwardAsync(WebhookDelivery delivery)
    {
        if (!_decryptor.IsEnabled)
        {
            var fallback = _resolver.Resolve(null);
            _logger.LogWarning("HyperPay webhook secret is disabled; forwarding unread to default {Url}", fallback.Url);
            await DeliverAsync(fallback.Url, delivery);
            return;
        }

        var headers = new Dictionary<string, string>(delivery.Headers, StringComparer.OrdinalIgnoreCase);
        headers.TryGetValue(HyperPayDecryptor.IvHeader, out var iv);
        headers.TryGetValue(HyperPayDecryptor.TagHeader, out var tag);

        var json = _decryptor.Decrypt(delivery.Body, iv, tag);
        if (json is null)
        {
            _logger.LogWarning("HyperPay webhook dropped: it could not be decrypted with the configured secret");
            return;
        }

        string? identifier;
        try
        {
            identifier = _resolver.ReadHyperPayIdentifier(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("HyperPay webhook dropped: decrypted body is not JSON ({Reason})", ex.Message);
            return;
        }

        var route = _resolver.Resolve(identifier);
        _logger.LogInformation(
            "HyperPay webhook {EventType} with identifier {Identifier} routing to {Url} (default: {IsDefault})",
            ReadEventType(json), identifier ?? "(none)", route.Url, route.IsDefault);

        await DeliverAsync(route.Url, delivery);
    }

    private async Task DeliverAsync(string url, WebhookDelivery delivery)
    {
        var status = await _forwarder.ForwardAsync(url, delivery);
        _logger.LogInformation("HyperPay webhook delivered to {Url} with status {Status}", url, status);
    }

    private static string? ReadEventType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
    }
}
