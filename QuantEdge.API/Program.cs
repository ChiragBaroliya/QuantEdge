using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Extensions;
using QuantEdge.Infrastructure.Hubs;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Configure centralized Serilog logging
    builder.Services.AddQuantEdgeLogging(builder.Configuration, "API");

    Log.Information("Starting QuantEdge.API...");

    // Add services to the container.

    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();
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
            policy.SetIsOriginAllowed(origin => new Uri(origin).Host == "localhost")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // Required for SignalR
        });
    });

    // Register QuantEdge.MarketData Clean Architecture services
    builder.Services.AddMarketDataServices(builder.Configuration);

    var app = builder.Build();

    // Perform automatic database provisioning on startup
    using (var scope = app.Services.CreateScope())
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await dbInitializer.InitializeAsync();
    }

    // Configure the HTTP request pipeline.
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "QuantEdge API v1");
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
