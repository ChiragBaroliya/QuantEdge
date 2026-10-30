using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace QuantEdge.Infrastructure.Security;

/// <summary>
/// Every API endpoint needs a valid <see cref="ApiAccessToken"/> unless it is marked [AllowAnonymous]. A non-admin caller
/// can't name another user in ?userId=. Only pass or reject: HttpContext.User stays empty on purpose, because the
/// controllers fall back to the user's claims, and Auto Paper's bot reads the shared "default_user" profile - a user
/// identity here would point its pages at different data than the bot uses.
/// ApiAuth:Enforce = false only logs what would be rejected, for rolling this out.
/// </summary>
public class ApiAccessTokenMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiAccessTokenMiddleware> _logger;

    public ApiAccessTokenMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<ApiAccessTokenMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint == null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        bool enforce = !string.Equals(_configuration[ApiAccessToken.EnforceSetting], "false", StringComparison.OrdinalIgnoreCase);
        var (status, problem) = Check(context);
        if (problem == null)
        {
            await _next(context);
            return;
        }

        if (!enforce)
        {
            _logger.LogWarning("API access check (log only) would reject {Method} {Path} with {Status}: {Problem}",
                context.Request.Method, context.Request.Path, status, problem);
            await _next(context);
            return;
        }

        _logger.LogWarning("API access rejected for {Method} {Path} from {RemoteIp} with {Status}: {Problem}",
            context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, status, problem);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message = problem });
    }

    private (int Status, string? Problem) Check(HttpContext context)
    {
        string? key = ApiAccessToken.SigningKey(_configuration);
        if (key == null)
        {
            return (StatusCodes.Status503ServiceUnavailable, $"{ApiAccessToken.SigningKeySetting} is not configured (at least {ApiAccessToken.MinSigningKeyLength} characters).");
        }

        string header = context.Request.Headers.Authorization.ToString();
        string? token = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..].Trim() : null;
        if (!ApiAccessToken.TryValidate(token, key, DateTime.UtcNow, out var caller))
        {
            return (StatusCodes.Status401Unauthorized, "Sign in again: this request has no valid access token.");
        }

        if (!caller!.MayActForAnyUser
            && int.TryParse(context.Request.Query["userId"].ToString(), out int requested)
            && requested != caller.UserId)
        {
            return (StatusCodes.Status403Forbidden, "You can only access your own data.");
        }

        return (StatusCodes.Status200OK, null);
    }
}
