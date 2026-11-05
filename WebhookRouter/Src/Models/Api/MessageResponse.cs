namespace WebhookRouter.Models.Api;

public class MessageResponse
{
    public string Message { get; set; } = string.Empty;
    public MessageResponse() { }
    public MessageResponse(string message) => Message = message;
}
