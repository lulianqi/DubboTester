using DubboTester.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace DubboTester.Hosting;

public static class HttpGatewayEndpointExtensions
{
    public static IEndpointRouteBuilder MapDubboTesterHttpGateway(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map("/api/dubbo/{**route}", async (HttpContext context, HttpGatewayService gateway) =>
        {
            string target = context.Features.Get<IHttpRequestFeature>()?.RawTarget
                ?? context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
            var result = await gateway.HandleAsync(target, context.Request.Method,
                context.Request.Headers.Authorization.ToString(), context.Request.ContentType,
                context.Request.Body, context.Connection.RemoteIpAddress?.ToString() ?? "",
                context.RequestAborted);
            context.Response.StatusCode = result.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            if (result.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            if (result.StatusCode == 405) context.Response.Headers.Allow = "POST";
            await context.Response.WriteAsJsonAsync(result, HttpGatewayService.JsonOptions, context.RequestAborted);
        });
        return endpoints;
    }
}
