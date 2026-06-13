using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.Extensions;

/// <summary>
/// Service collection extension class providing elegant Clean Architecture DI registrations
/// for the QuantEdge.MarketData module.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all required Market Data simulation, aggregation, routing services, 
    /// persistence repositories, and background hosted workers to the service collection container.
    /// </summary>
    public static IServiceCollection AddMarketDataServices(
        this IServiceCollection services, 
        IConfiguration configuration,
        string? jobType = null)
    {
        string? actualJobType = jobType;
        string? specifiedTimeframe = null;
        if (jobType != null && jobType.Contains(":"))
        {
            var parts = jobType.Split(':');
            actualJobType = parts[0];
            specifiedTimeframe = parts[1];
        }

        // Configure options mapping from the Configuration section 'MarketDataSettings:BrokerConfig'
        var brokerConfigSection = configuration.GetSection("MarketDataSettings:BrokerConfig");
        services.Configure<BrokerConfig>(options =>
        {
            brokerConfigSection.Bind(options);
            if (!string.IsNullOrEmpty(specifiedTimeframe))
            {
                options.Timeframes = new[] { specifiedTimeframe };
            }
        });

        // Enable Dapper snake_case mapping to PascalCase properties globally
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Register persistence layer
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddTransient<IMarketCandleRepository, MarketCandleRepository>();
        services.AddTransient<IMarketIndicatorRepository, MarketIndicatorRepository>();
        services.AddTransient<ITradingSignalRepository, TradingSignalRepository>();
        services.AddTransient<IStockMasterRepository, StockMasterRepository>();
        services.AddTransient<IZerodhaSessionRepository, ZerodhaSessionRepository>();
        services.AddTransient<IIndianHolidayRepository, IndianHolidayRepository>();
        services.AddTransient<IIndicatorService, IndicatorService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IMarketHoursService, MarketHoursService>();

        // Register caching infrastructure
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();


        // Register new WebSocket integration infrastructure
        services.AddSingleton<IReconnectPolicyService, ReconnectPolicyService>();
        services.AddSingleton<WebSocketConnectionManager>();

        // Dynamically register live WebSocket market data feed based on ActiveBroker config
        var activeBroker = brokerConfigSection.GetValue<string>("ActiveBroker") ?? "ZERODHA";
        if (activeBroker.Equals("ZERODHA", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IWebSocketMarketDataService, ZerodhaWebSocketMarketDataService>();
        }
        else
        {
            services.AddSingleton<IWebSocketMarketDataService, WebSocketMarketDataService>();
        }

        // Register historical data syncing service
        services.AddSingleton<IHistoricalDataService, ZerodhaHistoricalDataService>();
        services.AddTransient<IInstrumentSyncService, InstrumentSyncService>();

        // Register core thread-safe singleton services
        services.AddSingleton<ICandleBuilderService, CandleBuilderService>();
        services.AddSingleton<IMarketDataProcessor, MarketDataProcessor>();

        // Register Signal Engine Services
        services.AddTransient<SignalScoreCalculator>();
        services.AddTransient<ISignalEngineService, SignalEngineService>();

        return services;
    }
}
