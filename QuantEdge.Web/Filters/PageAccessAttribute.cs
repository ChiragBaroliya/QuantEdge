using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using QuantEdge.Web.Services;

namespace QuantEdge.Web.Filters;

/// <summary>
/// Lets a request through only when the signed-in user's role has access to the page
/// (see AppPages and the Role Permissions screen). Replaces the hard-coded [Authorize(Roles = "Admin")].
/// Unauthenticated -> login; not allowed -> Access Denied.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class PageAccessAttribute : Attribute, IAsyncAuthorizationFilter
{
    public string PageKey { get; }

    public PageAccessAttribute(string pageKey)
    {
        PageKey = pageKey ?? throw new ArgumentNullException(nameof(pageKey));
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // A method-level [PageAccess] overrides the controller-level one.
        if (context.Filters is { } filters)
        {
            PageAccessAttribute? effective = null;
            foreach (var f in filters)
                if (f is PageAccessAttribute p) effective = p;
            if (effective != null && !ReferenceEquals(effective, this)) return;
        }

        var user = context.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var permissions = context.HttpContext.RequestServices.GetRequiredService<IPagePermissionService>();
        if (!await permissions.CanAccessAsync(user, PageKey))
        {
            context.Result = new ForbidResult();
        }
    }
}
