using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayimChat.Services;

/// <summary>
/// 第三方系统集成接口的 ApiKey 校验。
/// 调用方在请求头携带 X-Api-Key，值需与配置文件 Integration:ApiKey 一致。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = config["Integration:ApiKey"];
        var provided = context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();

        if (string.IsNullOrEmpty(expected) || provided != expected)
        {
            context.Result = new ObjectResult(new { code = 401, msg = "ApiKey 无效或缺失（请在请求头携带 X-Api-Key）" })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        await next();
    }
}
