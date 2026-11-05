namespace WebhookRouter.Models.Jobs;

public class WebhookDelivery
{
    public string Body { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public DateTime ReceivedAt { get; set; }
}
