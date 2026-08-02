using System;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using QuantEdge.Infrastructure.Extensions;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Services;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure centralized Serilog logging
    builder.Services.AddQuantEdgeLogging(builder.Configuration, "Web");

    Log.Information("Starting QuantEdge.Web...");

    // Register Memory Cache & Clean Architecture Services
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ICacheService, MemoryCacheService>();
    builder.Services.AddMarketDataServices(builder.Configuration);

    // Add Cookie Authentication
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.LogoutPath = "/Account/Logout";
            options.ExpireTimeSpan = TimeSpan.FromDays(365);
            options.SlidingExpiration = true;
            options.Cookie.HttpOnly = true;
            options.Cookie.Name = "QuantEdge.Auth";
        });

    // Add MVC
    builder.Services.AddControllersWithViews();

    // Register a named HttpClient pointed at QuantEdge.API
    var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:44370";
    builder.Services.AddHttpClient("QuantEdgeApi", client =>
    {
        client.BaseAddress = new Uri(apiBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Accept self-signed dev certificates for localhost
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

    var app = builder.Build();

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Web Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
