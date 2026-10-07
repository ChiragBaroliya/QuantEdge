using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Services.Backtest;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Backtesting (Plan Phase 7). POST queues a run; the worker replays it on stored candles (no Zerodha call) and the page
/// polls GET runs/{id} for progress and the result.
/// </summary>
[ApiController]
[Route("backtest")]
public class BacktestController : ControllerBase
{
    private readonly IBacktestService _backtestService;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(IBacktestService backtestService, ILogger<BacktestController> logger)
    {
        _backtestService = backtestService ?? throw new ArgumentNullException(nameof(backtestService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] BacktestParams input, [FromQuery] int userId = 1)
    {
        try
        {
            int id = await _backtestService.QueueAsync(input ?? new BacktestParams(), userId);
            return Ok(new { id });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queueing a backtest failed.");
            return StatusCode(500, $"Queueing the backtest failed (apply backtest_runs / backtest_trades from schema.sql): {ex.Message}");
        }
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] int limit = 30)
    {
        var runs = await _backtestService.ListRunsAsync(limit);
        return Ok(runs.Select(ToDto));
    }

    [HttpGet("runs/{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var run = await _backtestService.GetRunAsync(id);
        return run == null ? NotFound() : Ok(ToDto(run));
    }

    [HttpGet("runs/{id:int}/trades")]
    public async Task<IActionResult> Trades(int id) => Ok(await _backtestService.GetTradesAsync(id));

    [HttpGet("runs/{id:int}/trades.csv")]
    public async Task<IActionResult> TradesCsv(int id)
    {
        var trades = await _backtestService.GetTradesAsync(id);
        var sb = new StringBuilder("Symbol,Sector,Regime,Score,Entry (UTC),Entry price,Exit (UTC),Exit price,Exit,Qty,Stop,Target,Sessions,Gross,Charges,Net,R,MFE %,MAE %\n");
        string Q(string? s) => "\"" + (s ?? string.Empty).Replace("\"", "\"\"") + "\"";
        foreach (var t in trades)
        {
            sb.AppendLine(string.Join(",", Q(t.Symbol), Q(t.Sector), Q(t.Regime), t.Score,
                t.EntryTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), t.EntryPrice.ToString(CultureInfo.InvariantCulture),
                t.ExitTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), t.ExitPrice.ToString(CultureInfo.InvariantCulture),
                Q(t.ExitReason), t.Quantity, t.StopLoss.ToString(CultureInfo.InvariantCulture), t.Target.ToString(CultureInfo.InvariantCulture),
                t.SessionsHeld, t.GrossPnl.ToString(CultureInfo.InvariantCulture), t.Charges.ToString(CultureInfo.InvariantCulture),
                t.NetPnl.ToString(CultureInfo.InvariantCulture), t.RMultiple.ToString(CultureInfo.InvariantCulture),
                t.MfePct.ToString(CultureInfo.InvariantCulture), t.MaePct.ToString(CultureInfo.InvariantCulture)));
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"backtest-{id}-trades.csv");
    }

    /// <summary>Queued → cancelled; running → asked to stop; finished → deleted with its trades.</summary>
    [HttpDelete("runs/{id:int}")]
    public async Task<IActionResult> Delete(int id) => await _backtestService.CancelOrDeleteAsync(id) ? Ok() : NotFound();

    private static object ToDto(BacktestRunRow r) => new
    {
        r.Id,
        r.Label,
        r.Status,
        r.ProgressPct,
        r.Message,
        r.Error,
        r.CreatedAt,
        r.StartedAt,
        r.FinishedAt,
        Params = JsonSerializer.Deserialize<JsonElement>(r.ParamsJson),
        Summary = string.IsNullOrEmpty(r.SummaryJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.SummaryJson)
    };
}
