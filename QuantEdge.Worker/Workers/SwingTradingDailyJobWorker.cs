using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Worker.Workers;

public class SwingTradingDailyJobWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SwingTradingDailyJobWorker> _logger;

    public SwingTradingDailyJobWorker(
        IServiceProvider serviceProvider,
        ILogger<SwingTradingDailyJobWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SwingTradingDailyJobWorker background service starting up...");

        // Brief startup delay for system initialization
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime nowIst = GetIstTime();
                DateTime targetTimeIst = new DateTime(nowIst.Year, nowIst.Month, nowIst.Day, 15, 45, 0);

                if (nowIst > targetTimeIst)
                {
                    targetTimeIst = targetTimeIst.AddDays(1);
                }

                TimeSpan delay = targetTimeIst - nowIst;
                _logger.LogInformation("Next Swing Trading EOD Job scheduled at {TargetTime} IST (Delay: {Delay})", targetTimeIst, delay);

                await Task.Delay(delay, stoppingToken);

                _logger.LogInformation("Market closed. Executing EOD Swing Trading Job...");
                using (var scope = _serviceProvider.CreateScope())
                {
                    var swingTradingService = scope.ServiceProvider.GetRequiredService<ISwingTradingService>();
                    await swingTradingService.RunEodJobAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SwingTradingDailyJobWorker loop. Retrying in 1 hour...");
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static DateTime GetIstTime()
    {
        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);
        }
        catch
        {
            // Fallback for systems where 'India Standard Time' timezone ID is not registered
            // UTC+5:30 offset
            return DateTime.UtcNow.AddHours(5).AddMinutes(30);
        }
    }
}
