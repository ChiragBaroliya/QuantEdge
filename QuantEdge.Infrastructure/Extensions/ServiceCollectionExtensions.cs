using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
        services.AddTransient<IUserRepository, UserRepository>();
        services.AddTransient<ISwingSlotRecommendationRepository, SwingSlotRecommendationRepository>();
        services.AddTransient<ISwingStrategySettingsRepository, SwingStrategySettingsRepository>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.AddTransient<IIndicatorService, IndicatorService>();
        services.AddSingleton<IMarketHoursService, MarketHoursService>();

        // Register SignalR infrastructure
        services.AddSignalR();

        // Register caching infrastructure
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();
        services.AddSingleton<IMarketDataCacheService, MarketDataCacheService>();


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
        services.AddTransient<ISwingTradingService, SwingTradingService>();

        // Register Paper Trading Infrastructure Services
        services.AddTransient<IPaperTradingRepository, PaperTradingRepository>();
        services.AddTransient<IAutoTradeRepository, AutoTradeRepository>();
        services.AddTransient<PaperOrderValidator>();
        services.AddSingleton<PaperMatchingEngine>();
        services.AddHttpClient();

        // Zerodha's Kite Connect API rejects requests from IPs outside the app's whitelist.
        // On dual-stack Linux hosts, the default HttpClient can resolve api.kite.trade to an
        // IPv6 address and connect over the server's (unwhitelisted) IPv6 address instead of
        // its whitelisted IPv4 one, causing real orders to be silently rejected. Forcing this
        // client's connections to IPv4 keeps every Kite Connect call on the whitelisted IP.
        services.AddHttpClient(ZerodhaKiteBrokerService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(
                        context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    if (addresses.Length == 0)
                    {
                        throw new SocketException((int)SocketError.HostNotFound);
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });
        services.AddSingleton<IZerodhaKiteBrokerService, ZerodhaKiteBrokerService>();
        services.AddSingleton<ITradingBrokerService, ZerodhaKiteBrokerService>();
        services.AddSingleton<IPaperTradingService, PaperTradingService>();
        services.AddSingleton<IAutoTradeService, AutoTradeService>();

        // Register Real Trading Infrastructure Services
        services.AddTransient<IRealTradingRepository, RealTradingRepository>();
        services.AddSingleton<IRealTradeCacheService, RealTradeCacheService>();
        services.AddSingleton<IAutoRealTradeService, AutoRealTradeService>();

        // Register Trading Reports & Performance Analytics
        services.AddTransient<ITradingReportRepository, TradingReportRepository>();
        services.AddTransient<ITradingReportService, TradingReportService>();

        services.AddTransient<ICandleSummaryService, CandleSummaryService>();

        return services;
    }
}

