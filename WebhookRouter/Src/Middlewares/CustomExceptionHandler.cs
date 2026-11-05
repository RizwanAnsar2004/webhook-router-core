using System.Text.Json;
using WebhookRouter.Models.Api;

namespace WebhookRouter.Middlewares;

public class ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var statusCode = ex is ApiException apiEx ? apiEx.StatusCode : StatusCodes.Status500InternalServerError;
            if (statusCode >= 500) logger.LogError(ex, "Unhandled error on {Path}", context.Request.Path);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            var message = ex is ApiException ? ex.Message : "An unexpected error occurred.";
            var body = new Response<object> { StatusCode = statusCode, Message = message, Success = false };
            await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
        }
    }
}

public class ApiException(string message) : Exception(message)
{
    public int StatusCode { get; set; }

    public static ApiException Create(int statusCode, string message)
        => new(message) { StatusCode = statusCode };
}
