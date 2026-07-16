using QuantEdge.Infrastructure.Extensions;
using Serilog;
using Microsoft.AspNetCore.HttpOverrides;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure centralized Serilog logging
    builder.Services.AddQuantEdgeLogging(builder.Configuration, "Web");

    Log.Information("Starting QuantEdge.Web...");

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
    app.UseAuthorization();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Token}/{action=Index}/{id?}");

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
