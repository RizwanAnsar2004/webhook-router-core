using System.Text;
using Microsoft.AspNetCore.Mvc;
using WebhookRouter.Models.Api;
using WebhookRouter.Services;

namespace WebhookRouter.Controllers;

[ApiController]
[Route("v1/api/webhooks")]
public class WebhooksController(WebhookIntakeService _intake) : ControllerBase
{
    #region HyperPay
    [HttpPost("hyperpay")]
    [EndpointSummary("Receives a HyperPay webhook, queues it for routing and acknowledges immediately")]
    public async Task<MessageResponse> HyperPayAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        _intake.EnqueueHyperPay(body, Request.ContentType, Request.Headers);
        return new MessageResponse("Accepted.");
    }
    #endregion
}
