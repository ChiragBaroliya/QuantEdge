using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Extensions;
using QuantEdge.Infrastructure.Hubs;
using Serilog;
using Microsoft.AspNetCore.HttpOverrides;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure centralized Serilog logging
    builder.Services.AddQuantEdgeLogging(builder.Configuration, "API");

    Log.Information("Starting QuantEdge.API...");

    // Add services to the container.

    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // In-memory cache — used to store the Web UI returnUrl between login-url call and Zerodha callback
    builder.Services.AddMemoryCache();

    // Register SignalR
    builder.Services.AddSignalR();

    // Configure CORS to allow access from local Web application dynamically
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.SetIsOriginAllowed(origin =>
            {
                var host = new Uri(origin).Host;
                return host == "localhost" || host == "quantage.cittaserver.com";
            })
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // Required for SignalR
        });
    });

    // Register QuantEdge.MarketData Clean Architecture services
    builder.Services.AddMarketDataServices(builder.Configuration);

    var app = builder.Build();

    app.UsePathBase("/api");

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    // Perform automatic database provisioning on startup
    using (var scope = app.Services.CreateScope())
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await dbInitializer.InitializeAsync();
    }

    // Configure the HTTP request pipeline.
    app.UseSwagger(c =>
    {
        c.RouteTemplate = "swagger/{documentName}/swagger.json";
    });
    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = "swagger";
        c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "QuantEdge API v1");
    });

    app.UseHttpsRedirection();

    // Enable CORS before authorization and endpoint mappings
    app.UseCors();

    app.UseAuthorization();

    app.MapControllers();

    // Map SignalR Hub
    app.MapHub<MarketDataHub>("/hubs/marketdata");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
