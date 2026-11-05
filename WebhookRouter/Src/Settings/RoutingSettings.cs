namespace WebhookRouter.Settings;

public class RoutingSettings
{
    public string DefaultUrl { get; set; } = string.Empty;
    public List<ServiceRoute> Services { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
}

public class ServiceRoute
{
    public string Identifier { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
