using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Worker.Workers;

public class SwingTradingIntradayJobWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SwingTradingIntradayJobWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(30);

    public SwingTradingIntradayJobWorker(
        IServiceProvider serviceProvider,
        ILogger<SwingTradingIntradayJobWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SwingTradingIntradayJobWorker (30-Minute Job) background service starting up...");

        // Startup delay
        await Task.Delay(10000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime nowIst = GetIstTime();

                if (IsWithinTradingWindow(nowIst))
                {
                    _logger.LogInformation("Market open ({Time} IST). Running 30-Minute Swing Trading Intraday Job...", nowIst.ToString("HH:mm:ss"));

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var swingTradingService = scope.ServiceProvider.GetRequiredService<ISwingTradingService>();
                        var holidayRepo = scope.ServiceProvider.GetService<IIndianHolidayRepository>();

                        bool isHoliday = holidayRepo != null && await holidayRepo.IsHolidayAsync(nowIst.Date);
                        if (isHoliday)
                        {
                            _logger.LogInformation("Today ({Date}) is a Market Holiday. Skipping 30-minute Swing Trading scan.", nowIst.ToString("yyyy-MM-dd"));
                        }
                        else
                        {
                            await swingTradingService.RunIntraday30MinJobAsync(stoppingToken);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("Outside Market Trading Window ({Time} IST). 30-Minute Swing Trading scan waiting...", nowIst.ToString("HH:mm:ss"));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SwingTradingIntradayJobWorker loop.");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private static bool IsWithinTradingWindow(DateTime istTime)
    {
        if (istTime.DayOfWeek == DayOfWeek.Saturday || istTime.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        TimeSpan start = new TimeSpan(9, 15, 0); // 09:15 AM
        TimeSpan end = new TimeSpan(15, 30, 0);  // 03:30 PM

        TimeSpan nowTime = istTime.TimeOfDay;
        return nowTime >= start && nowTime <= end;
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
            return DateTime.UtcNow.AddHours(5).AddMinutes(30);
        }
    }
}
