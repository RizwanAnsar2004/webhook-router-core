using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WebhookRouter.Models.Api;

namespace WebhookRouter.Middlewares;

public class ResponseWrapperFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult obj
            && obj.Value is not null
            && obj.Value is not ProblemDetails
            && !IsAlreadyWrapped(obj.Value.GetType()))
        {
            var statusCode = obj.StatusCode ?? StatusCodes.Status200OK;
            context.Result = new ObjectResult(Response<object>.CreateSuccess(obj.Value, null, statusCode))
            {
                StatusCode = statusCode
            };
        }

        await next();
    }

    private static bool IsAlreadyWrapped(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Response<>);
}
