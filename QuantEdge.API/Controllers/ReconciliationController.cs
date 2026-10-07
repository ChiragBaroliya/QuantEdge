using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Reconciliation (Plan L.6 / L.7): our open real positions vs Zerodha, and the data-quality issues found by the
/// daily checks (stored daily closes vs NSE's official close, positions vs Zerodha holdings).
/// </summary>
[ApiController]
[Route("reconciliation")]
public class ReconciliationController : ControllerBase
{
    // One live comparison costs 2 Kite calls (holdings + positions); repeat requests within this window reuse it.
    private static readonly TimeSpan LiveCompareCacheFor = TimeSpan.FromSeconds(60);

    private readonly IPositionReconciliationService _reconciliationService;
    private readonly IDataQualityRepository _dataQualityRepository;
    private readonly ICacheService _cache;
    private readonly ILogger<ReconciliationController> _logger;

    public ReconciliationController(IPositionReconciliationService reconciliationService, IDataQualityRepository dataQualityRepository,
        ICacheService cache, ILogger<ReconciliationController> logger)
    {
        _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
        _dataQualityRepository = dataQualityRepository ?? throw new ArgumentNullException(nameof(dataQualityRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Open real positions vs Zerodha right now (cached 60 s per user to protect the Kite rate limit).</summary>
    [HttpGet("positions")]
    public async Task<IActionResult> GetPositions([FromQuery] int userId = 1)
    {
        try
        {
            string key = $"reconciliation:positions:{userId}";
            var cached = await _cache.GetAsync<PositionReconciliationResult>(key);
            if (cached != null) return Ok(cached);

            var result = await _reconciliationService.CompareAsync(userId);
            if (result.Success) await _cache.SetAsync(key, result, LiveCompareCacheFor);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Position reconciliation failed for user {UserId}.", userId);
            return StatusCode(500, $"Position reconciliation failed: {ex.Message}");
        }
    }

    /// <summary>Data-quality issues recorded by the daily checks over the last <paramref name="days"/> days.</summary>
    [HttpGet("issues")]
    public async Task<IActionResult> GetIssues([FromQuery] int days = 7)
    {
        try
        {
            return Ok(await _dataQualityRepository.GetRecentAsync(Math.Clamp(days, 1, 60)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading data-quality issues failed.");
            return StatusCode(500, $"Loading data-quality issues failed (apply data_quality_issues in schema.sql): {ex.Message}");
        }
    }
}
