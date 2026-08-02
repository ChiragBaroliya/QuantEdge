using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Extensions;
using QuantEdge.Infrastructure.Hubs;
using Serilog;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

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

    // Register ASP.NET Core Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database");

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

    app.UseMiddleware<QuantEdge.API.Middleware.PaperTradingExceptionMiddleware>();

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    // Configure Swagger conditionally based on Enable_Swagger flag in appsettings.json
    var enableSwagger = builder.Configuration.GetValue<bool>("Enable_Swagger", true);
    if (enableSwagger)
    {
        app.UseSwagger(c =>
        {
            c.RouteTemplate = "swagger/{documentName}/swagger.json";
        });
        app.UseSwaggerUI(c =>
        {
            c.RoutePrefix = "swagger";
            c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "QuantEdge API v1");
        });
    }

    app.UseHttpsRedirection();

    // Enable CORS before authorization and endpoint mappings
    app.UseCors();

    app.UseAuthorization();

    app.MapControllers();

    // Map /health endpoint for production monitoring
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";

            var response = new
            {
                status = report.Status.ToString(),
                service = "QuantEdge.API",
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 2),
                    error = e.Value.Exception?.Message
                })
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await context.Response.WriteAsync(json);
        }
    });

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

/// <summary>
/// Production health check implementation to verify PostgreSQL database connection status.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public DatabaseHealthCheck(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();
            return Task.FromResult(HealthCheckResult.Healthy("PostgreSQL database connection is operational"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("PostgreSQL database connection failed", ex));
        }
    }
}

