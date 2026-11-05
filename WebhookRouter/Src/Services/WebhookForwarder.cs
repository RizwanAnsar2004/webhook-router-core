using System.Net.Http.Headers;
using System.Text;
using WebhookRouter.Models.Jobs;

namespace WebhookRouter.Services;

public class WebhookForwarder(HttpClient http)
{
    public async Task<int> ForwardAsync(string url, WebhookDelivery delivery, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(delivery.Body));
        if (!string.IsNullOrWhiteSpace(delivery.ContentType))
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(delivery.ContentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        foreach (var (name, value) in delivery.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                content.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var excerpt = body.Length > 500 ? body[..500] : body;
            throw new HttpRequestException($"Target {url} answered {status}: {excerpt}", null, response.StatusCode);
        }

        return status;
    }
}
