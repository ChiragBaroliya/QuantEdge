using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>Where a Zerodha problem happened.</summary>
public static class BrokerApiSource
{
    public const string Rest = "REST";
    public const string Historical = "HISTORICAL";
    public const string WebSocket = "WEBSOCKET";
    public const string Session = "SESSION";
    public const string Job = "JOB";
    public const string Nse = "NSE";
}

public interface IBrokerApiEventRecorder
{
    /// <summary>
    /// Records a failed, rate-limited or skipped Zerodha call so the header bell can show it. Never throws - a
    /// notification problem must never break trading. Repeats of the same failure within the de-duplication
    /// window are counted and folded into the next row instead of being written one by one.
    /// </summary>
    void RecordFailure(string source, string operation, string? message, int? httpStatus = null,
        string? symbol = null, int? userId = null, string level = "error");
}

public sealed class BrokerApiEventRecorder : IBrokerApiEventRecorder
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);
    private static readonly string ProcessName = SafeProcessName();

    private readonly IBrokerApiEventRepository _repository;
    private readonly IHubBroadcastService? _hubBroadcast;
    private readonly ILogger<BrokerApiEventRecorder> _logger;

    // key -> (when the last row was written, repeats suppressed since then)
    private readonly ConcurrentDictionary<string, (DateTime WrittenUtc, int Suppressed)> _recent = new();

    public BrokerApiEventRecorder(IBrokerApiEventRepository repository, ILogger<BrokerApiEventRecorder> logger, IHubBroadcastService? hubBroadcast = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubBroadcast = hubBroadcast;
    }

    public void RecordFailure(string source, string operation, string? message, int? httpStatus = null,
        string? symbol = null, int? userId = null, string level = "error")
    {
        try
        {
            var now = DateTime.UtcNow;
            string key = $"{source}|{operation}|{httpStatus}|{symbol}";
            int repeats = 0;
            bool write = true;

            _recent.AddOrUpdate(key,
                _ => (now, 0),
                (_, prev) =>
                {
                    if (now - prev.WrittenUtc < DedupWindow)
                    {
                        write = false;
                        return (prev.WrittenUtc, prev.Suppressed + 1);
                    }
                    repeats = prev.Suppressed;
                    return (now, 0);
                });

            if (!write) return;

            var evt = new BrokerApiEvent
            {
                OccurredAt = now,
                Source = source,
                Operation = operation,
                Level = level,
                HttpStatus = httpStatus,
                Symbol = symbol,
                UserId = userId,
                Message = message,
                RepeatCount = 1 + repeats,
                ProcessName = ProcessName
            };

            // Fire-and-forget: callers are often on a trading hot path.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _repository.InsertAsync(evt);
                    if (_hubBroadcast != null)
                    {
                        await _hubBroadcast.BroadcastAllAsync("ReceiveBrokerApiAlert", new
                        {
                            source, operation, level, httpStatus, symbol, userId, message, occurredAt = now
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not record Zerodha API event {Source} {Operation} (apply broker_api_events in schema.sql).", source, operation);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrokerApiEventRecorder failed for {Source} {Operation}.", source, operation);
        }
    }

    private static string SafeProcessName()
    {
        try
        {
            string jobType = Environment.GetCommandLineArgs() is { Length: > 1 } args ? string.Join(" ", args[1..]) : string.Empty;
            return $"{Process.GetCurrentProcess().ProcessName} {jobType}".Trim();
        }
        catch
        {
            return "QuantEdge";
        }
    }
}
