using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("signals")]
public class TradingSignalController : ControllerBase
{
    private readonly ISignalEngineService _signalEngine;
    private readonly ILogger<TradingSignalController> _logger;

    public TradingSignalController(
        ISignalEngineService signalEngine,
        ILogger<TradingSignalController> logger)
    {
        _signalEngine = signalEngine ?? throw new ArgumentNullException(nameof(signalEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Triggers the technical analysis Signal Engine on demand for a symbol and timeframe,
    /// returns the weighted evaluation breakdown, and persists any generated BUY/SELL recommendation.
    /// </summary>
    /// <param name="symbol">Stock/Asset Symbol (e.g. NIFTY, BANKNIFTY)</param>
    /// <param name="timeframe">Interval (e.g. 1m, 5m)</param>
    [HttpGet("evaluate")]
    public async Task<IActionResult> Evaluate([FromQuery] string symbol, [FromQuery] string timeframe, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest("Query parameter 'symbol' cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return BadRequest("Query parameter 'timeframe' cannot be empty.");
        }

        _logger.LogInformation("HTTP Request: Evaluating trade recommendations for symbol {Symbol} ({Timeframe})", symbol, timeframe);

        try
        {
            SignalEvaluationResult result = await _signalEngine.EvaluateSignalAsync(symbol, timeframe, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run signal evaluation for symbol {Symbol}.", symbol);
            return StatusCode(500, $"An error occurred during evaluation: {ex.Message}");
        }
    }
}
