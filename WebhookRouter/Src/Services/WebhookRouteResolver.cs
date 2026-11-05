using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookRouter.Settings;

namespace WebhookRouter.Services;

public record ResolvedRoute(string? Identifier, string Url, bool IsDefault);

public class WebhookRouteResolver(IOptionsMonitor<RoutingSettings> routing, IOptionsMonitor<HyperPaySettings> hyperPay)
{
    #region Identifier
    public string? ReadHyperPayIdentifier(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var parameter = hyperPay.CurrentValue.IdentifierParameter;
        var payload = FindProperty(root, "payload");
        return ReadCustomParameter(payload, parameter) ?? ReadCustomParameter(root, parameter);
    }

    private static string? ReadCustomParameter(JsonElement? container, string parameter)
    {
        if (container is not { ValueKind: JsonValueKind.Object } element) return null;
        var customParameters = FindProperty(element, "customParameters");
        if (customParameters is not { ValueKind: JsonValueKind.Object } parameters) return null;
        var value = FindProperty(parameters, parameter);
        if (value is not { ValueKind: JsonValueKind.String } text) return null;
        var identifier = text.GetString()?.Trim();
        return string.IsNullOrEmpty(identifier) ? null : identifier;
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
    #endregion

    #region Routing
    public ResolvedRoute Resolve(string? identifier)
    {
        var settings = routing.CurrentValue;
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            var match = settings.Services.FirstOrDefault(s =>
                string.Equals(s.Identifier.Trim(), identifier, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.Url));
            if (match is not null) return new ResolvedRoute(match.Identifier.Trim(), match.Url.Trim(), false);
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultUrl))
            throw new InvalidOperationException($"No service is registered for identifier '{identifier}' and Routing:DefaultUrl is empty.");

        return new ResolvedRoute(identifier, settings.DefaultUrl.Trim(), true);
    }
    #endregion
}
