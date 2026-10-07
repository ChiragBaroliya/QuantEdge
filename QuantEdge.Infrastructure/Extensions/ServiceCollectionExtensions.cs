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
        string? jobType = null,
        bool isApiHost = false)
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
        services.AddTransient<IFavoriteSymbolRepository, FavoriteSymbolRepository>();
        services.AddTransient<ISectorRepository, SectorRepository>();
        services.AddTransient<ILiveQuoteRepository, LiveQuoteRepository>();
        services.AddSingleton<LiveQuoteRecorder>();
        services.AddTransient<IDayQuoteService, DayQuoteService>();
        services.AddTransient<IBrokerApiEventRepository, BrokerApiEventRepository>();
        services.AddSingleton<IBrokerApiEventRecorder, BrokerApiEventRecorder>();
        services.AddTransient<KiteApiFailureHandler>();

        // NSE archives (official bhavcopy). NSE rejects requests without a browser-like User-Agent.
        services.AddHttpClient(NseBhavcopyService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/zip,*/*");
        });
        services.AddTransient<INseBhavcopyService, NseBhavcopyService>();
        services.AddTransient<ICorporateActionService, CorporateActionService>();
        services.AddTransient<IDataQualityRepository, DataQualityRepository>();
        services.AddTransient<IPositionReconciliationService, PositionReconciliationService>();
        services.AddTransient<IMarketRegimeService, MarketRegimeService>();
        services.AddTransient<QuantEdge.Infrastructure.Services.Backtest.IBacktestService, QuantEdge.Infrastructure.Services.Backtest.BacktestService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.AddTransient<IIndicatorService, IndicatorService>();
        services.AddSingleton<IMarketHoursService, MarketHoursService>();

        // Register SignalR infrastructure
        services.AddSignalR();

        // IHubBroadcastService: QuantEdge.API hosts the actual MarketDataHub clients connect to,
        // so it can broadcast locally. QuantEdge.Worker is a headless process with no clients of
        // its own connected to any hub, so it must relay broadcasts over HTTP to the API instead
        // - otherwise SignalR sends from Worker-run jobs (e.g. Auto Real Trade) silently go nowhere.
        if (isApiHost)
        {
            services.AddSingleton<IHubBroadcastService, LocalHubBroadcastService>();
        }
        else
        {
            services.AddHttpClient(RemoteHubBroadcastService.HttpClientName);
            services.AddSingleton<IHubBroadcastService, RemoteHubBroadcastService>();
        }

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
        services.AddTransient<IStockVerdictService, StockVerdictService>();
        services.AddTransient<ISectorDashboardService, SectorDashboardService>();
        services.AddTransient<INotificationService, NotificationService>();

        // Register Paper Trading Infrastructure Services
        services.AddTransient<IPaperTradingRepository, PaperTradingRepository>();
        services.AddTransient<IAutoTradeRepository, AutoTradeRepository>();
        services.AddTransient<IManualPaperTradeRepository, ManualPaperTradeRepository>();
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
            })
            // Every failed / rate-limited Kite REST call is reported to the header bell (broker_api_events).
            .AddHttpMessageHandler<KiteApiFailureHandler>();
        services.AddSingleton<IZerodhaKiteBrokerService, ZerodhaKiteBrokerService>();
        services.AddSingleton<ITradingBrokerService, ZerodhaKiteBrokerService>();
        services.AddSingleton<IPaperTradingService, PaperTradingService>();
        services.AddSingleton<IAutoTradeService, AutoTradeService>();
        services.AddSingleton<IManualPaperTradeService, ManualPaperTradeService>();

        // Register Real Trading Infrastructure Services
        services.AddTransient<IRealTradingRepository, RealTradingRepository>();
        services.AddSingleton<IRealTradeCacheService, RealTradeCacheService>();
        services.AddSingleton<IAutoRealTradeService, AutoRealTradeService>();

        // Register Trading Reports & Performance Analytics
        services.AddTransient<IChargeRatesRepository, ChargeRatesRepository>();
        services.AddTransient<IRealOrderChargesRepository, RealOrderChargesRepository>();
        services.AddTransient<IRealOrderChargesService, RealOrderChargesService>();
        services.AddTransient<ITradingReportRepository, TradingReportRepository>();
        services.AddTransient<ITradingReportService, TradingReportService>();

        services.AddTransient<ICandleSummaryService, CandleSummaryService>();

        return services;
    }
}

