using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Market regime (Plan Phase 1): today's regime with its score breakdown, the recent history, the per-regime policy, and
/// whether the bot currently uses it (swing_strategy_settings.market_gate_mode). Database only - no Zerodha call.
/// </summary>
[ApiController]
[Route("market")]
public class MarketRegimeController : ControllerBase
{
    private readonly IMarketRegimeService _regimeService;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly ILogger<MarketRegimeController> _logger;

    public MarketRegimeController(IMarketRegimeService regimeService, ISwingStrategySettingsRepository strategySettingsRepository,
        ILogger<MarketRegimeController> logger)
    {
        _regimeService = regimeService ?? throw new ArgumentNullException(nameof(regimeService));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("regime")]
    public async Task<IActionResult> GetRegime([FromQuery] int days = 30)
    {
        try
        {
            var latest = await _regimeService.GetLatestAsync() ?? await _regimeService.ComputeAndStoreAsync();
            var history = await _regimeService.GetHistoryAsync(days);
            var policies = await _regimeService.GetPoliciesAsync();
            var strategy = await _strategySettingsRepository.GetSettingsAsync();
            return Ok(new
            {
                latest,
                history,
                policy = latest == null ? null : policies.FirstOrDefault(p => p.Regime == latest.Regime),
                policies,
                gateMode = strategy.MarketGateMode,
                botUsesRegime = strategy.UsesRegimeGate
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the market regime failed.");
            return StatusCode(500, $"Loading the market regime failed (apply market_regime_daily / regime_policy in schema.sql): {ex.Message}");
        }
    }
}
