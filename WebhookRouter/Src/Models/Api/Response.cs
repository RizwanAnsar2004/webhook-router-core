namespace WebhookRouter.Models.Api;

public class Response<T>
{
    public T? Data { get; set; }
    public string? Message { get; set; }
    public bool Success { get; set; }
    public int StatusCode { get; set; }

    public static Response<T> CreateSuccess(T data, string? message = null, int statusCode = 200)
        => new() { Data = data, Message = message, Success = true, StatusCode = statusCode };
}
