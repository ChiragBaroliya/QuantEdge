using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEdge.Infrastructure.Configurations;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Worker.Workers;

/// <summary>
/// Background Worker (JobType: "activezerodhatoken") that polls the database and
/// activates the Zerodha session token if a fresh token was created after 6:00 AM IST today.
///
/// <para>
/// <b>Flow:</b>
/// <list type="number">
///   <item>On startup, run an immediate activation check.</item>
///   <item>Then poll every <see cref="CheckIntervalSeconds"/> seconds.</item>
///   <item>Calls <c>sp_activate_zerodha_token</c> via repository.</item>
///   <item>Logs whether the token was activated, already active, or stale.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Rule:</b> A token is only promoted to <c>is_active = TRUE</c> when its
/// <c>created_at</c> timestamp is on or after today's 6:00 AM IST boundary.
/// This ensures tokens from the previous trading session are never reactivated.
/// </para>
/// </summary>
public class ActiveZerodhaTokenWorker : BackgroundService
{
    private readonly IZerodhaSessionRepository _sessionRepository;
    private readonly BrokerConfig _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ActiveZerodhaTokenWorker> _logger;

    /// <summary>How often the worker polls for a new token (in seconds). Default: 30s.</summary>
    private const int CheckIntervalSeconds = 30;

    public ActiveZerodhaTokenWorker(
        IZerodhaSessionRepository sessionRepository,
        IOptions<BrokerConfig> config,
        IHostApplicationLifetime lifetime,
        ILogger<ActiveZerodhaTokenWorker> logger)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ActiveZerodhaTokenWorker started. API Key: {ApiKey}. Allowed execution window: 6:00 AM - 7:00 AM IST.",
            _config.ApiKey);

        // Brief startup delay so DatabaseInitializer has time to complete
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Verify startup time is within the allowed window
        if (!IsWithinAllowedWindow(out DateTime startIst))
        {
            _logger.LogWarning(
                "ActiveZerodhaTokenWorker: Current time {Time} IST is outside the allowed window (6:00 AM - 7:00 AM IST). " +
                "This service runs only during that hour. Shutting down service.",
                startIst.ToString("hh:mm:ss tt"));
            _lifetime.StopApplication();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Verify execution time is still within the allowed window
            if (!IsWithinAllowedWindow(out DateTime currentIst))
            {
                _logger.LogInformation(
                    "ActiveZerodhaTokenWorker: Current time {Time} IST is past the allowed window (6:00 AM - 7:00 AM IST). " +
                    "Stopping service.",
                    currentIst.ToString("hh:mm:ss tt"));
                _lifetime.StopApplication();
                break;
            }

            try
            {
                await RunActivationCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ActiveZerodhaTokenWorker encountered an error during activation check. Will retry in {Interval}s.", CheckIntervalSeconds);
            }

            // Wait before next poll
            await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("ActiveZerodhaTokenWorker has stopped.");
    }

    private bool IsWithinAllowedWindow(out DateTime nowIst)
    {
        TimeZoneInfo indianTimeZone;
        try
        {
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Fallback for non-Windows environments
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }

        nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indianTimeZone);
        var timeOfDay = nowIst.TimeOfDay;
        return timeOfDay >= TimeSpan.FromHours(6) && timeOfDay <= TimeSpan.FromHours(7);
    }

    /// <summary>
    /// Executes one activation cycle:
    /// <list type="bullet">
    ///   <item>Calls <c>sp_activate_zerodha_token</c> for the configured API key.</item>
    ///   <item>Logs the outcome: activated / already active / stale / missing.</item>
    /// </list>
    /// </summary>
    private async Task RunActivationCheckAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _logger.LogWarning("ActiveZerodhaTokenWorker: ApiKey is not configured. Skipping check.");
            return;
        }

        // 1. Get the current time in IST to pass to helper checks
        TimeZoneInfo indianTimeZone;
        try
        {
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            indianTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indianTimeZone);

        _logger.LogDebug("ActiveZerodhaTokenWorker: Checking if a token is already active for today...");

        // 2. First check if a token is already active for today
        var activeSession = await _sessionRepository.GetActiveSessionAsync();
        if (activeSession is not null)
        {
            var sessionCreatedAtIst = TimeZoneInfo.ConvertTime(activeSession.CreatedAt, indianTimeZone);
            var cutoff = nowIst.Date.AddHours(6);

            // If the active token was created today after 6:00 AM IST, we are fully activated!
            if (sessionCreatedAtIst.Date == nowIst.Date && sessionCreatedAtIst >= cutoff)
            {
                _logger.LogInformation(
                    "ActiveZerodhaTokenWorker: ✅ A valid token is already active for today (created: {CreatedAt} IST). " +
                    "No further database check or polling is required. Shutting down service.",
                    sessionCreatedAtIst.ToString("yyyy-MM-dd hh:mm:ss tt"));
                _lifetime.StopApplication();
                return;
            }
            else
            {
                _logger.LogInformation(
                    "ActiveZerodhaTokenWorker: Stale active token found from yesterday or before 6:00 AM today (created: {CreatedAt} IST). " +
                    "Checking for a new token to activate...",
                    sessionCreatedAtIst.ToString("yyyy-MM-dd hh:mm:ss tt"));
            }
        }

        // 3. Attempt activation — stored function checks the 6 AM IST rule
        string? activatedToken = await _sessionRepository.ActivateTokenIfValidAsync(_config.ApiKey);

        if (!string.IsNullOrWhiteSpace(activatedToken))
        {
            _logger.LogInformation(
                "ActiveZerodhaTokenWorker: ✅ New token successfully ACTIVATED for API key {ApiKey}. " +
                "Token: {TokenMask}. The session is now live. Shutting down service as work is complete.",
                _config.ApiKey,
                MaskToken(activatedToken));
            
            _lifetime.StopApplication();
            return;
        }

        // 4. Log status if no token is active or eligible yet
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)); // IST
        var cutoffTime = now.Date.AddHours(6);
        var minutesUntilCutoff = (cutoffTime - now).TotalMinutes;

        if (now.TimeOfDay < TimeSpan.FromHours(6))
        {
            _logger.LogInformation(
                "ActiveZerodhaTokenWorker: ⏳ No active token for API key {ApiKey}. " +
                "6 AM IST activation window opens in {Minutes:F0} minute(s). " +
                "Please create a new token via QuantEdge.Web → Create Token.",
                _config.ApiKey,
                minutesUntilCutoff);
        }
        else
        {
            _logger.LogWarning(
                "ActiveZerodhaTokenWorker: ⚠️ No active token for API key {ApiKey}. " +
                "It is past 6 AM IST but no token created today was found. " +
                "Please create a new token via QuantEdge.Web → Create Token to resume market data.",
                _config.ApiKey);
        }
    }

    /// <summary>Masks a token for safe logging: first 6 chars + ••••• + last 4 chars.</summary>
    private static string MaskToken(string token) =>
        token.Length > 10
            ? $"{token[..6]}•••••{token[^4..]}"
            : "••••••••••";
}
