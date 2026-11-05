namespace WebhookRouter.Settings;

public class HyperPaySettings
{
    public bool WebhookSecretEnabled { get; set; } = true;
    public string WebhookSecret { get; set; } = string.Empty;
    public string IdentifierParameter { get; set; } = "SHOPPER_service";
}
