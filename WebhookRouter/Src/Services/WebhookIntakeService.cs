using Hangfire;
using WebhookRouter.Middlewares;
using WebhookRouter.Models.Jobs;

namespace WebhookRouter.Services;

public class WebhookIntakeService(IBackgroundJobClient _jobs)
{
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "TE", "Trailer", "Expect", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate"
    };

    public string EnqueueHyperPay(string body, string? contentType, IHeaderDictionary headers)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw ApiException.Create(StatusCodes.Status400BadRequest, "Webhook body is empty.");
        if (string.IsNullOrWhiteSpace(headers[HyperPayDecryptor.IvHeader]) || string.IsNullOrWhiteSpace(headers[HyperPayDecryptor.TagHeader]))
            throw ApiException.Create(StatusCodes.Status400BadRequest, "Webhook encryption headers are missing.");

        var delivery = new WebhookDelivery
        {
            Body = body,
            ContentType = contentType,
            Headers = headers
                .Where(h => !SkippedHeaders.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            ReceivedAt = DateTime.UtcNow
        };

        return _jobs.Enqueue<HyperPayWebhookJob>(job => job.ForwardAsync(delivery));
    }
}
