using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuantEdge.Infrastructure.Extensions;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Worker.Workers;
using Serilog;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Determine the job type based on command-line argument (history/marketdatafeed) or configuration
    string jobType = args.FirstOrDefault(a => !a.StartsWith("-")) 
                     ?? builder.Configuration["JobType"] 
                     ?? "marketdatafeed";

    // Sanitize jobType for distinct log filename
    string appLabel = "Worker";
    if (!string.IsNullOrEmpty(jobType))
    {
        appLabel = $"Worker_{jobType.Replace(":", "_")}";
    }

    // Configure centralized Serilog logging with unique application label
    builder.Services.AddQuantEdgeLogging(builder.Configuration, appLabel);

    // Enable native Windows Service lifecycle management and set service name
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = appLabel;
    });

    Log.Information("Starting QuantEdge.Worker with job: {JobType}", jobType);

    // Register all MarketData configurations, WebSocket ingest, candle aggregators, and persistence services
    builder.Services.AddMarketDataServices(builder.Configuration, jobType);

    // Register background hosted workers conditionally based on jobType argument
    if (!string.IsNullOrWhiteSpace(jobType))
    {
        string actualJobType = jobType;
        if (jobType.Contains(":"))
        {
            actualJobType = jobType.Split(':')[0];
        }

        if (actualJobType.Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<HistoricalDataSyncWorker>();
        }
        else if (actualJobType.Equals("marketdatafeed", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<MarketDataFeedWorker>();
        }
        else if (actualJobType.Equals("activezerodhatoken", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<ActiveZerodhaTokenWorker>();
        }
        else if (actualJobType.Equals("instrumentsync", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<InstrumentSyncWorker>();
        }

        // Also run the InstrumentSyncWorker in all other job conditions to support Monday morning scheduling
        // and immediate startup sync for testing.
        if (!actualJobType.Equals("instrumentsync", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddHostedService<InstrumentSyncWorker>();
        }
    }

    var host = builder.Build();

    // Perform automatic database provisioning on startup
    using (var scope = host.Services.CreateScope())
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await dbInitializer.InitializeAsync();
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
