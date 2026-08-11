using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

[ApiController]
[Route("CandleSummary")]
public class CandleSummaryController : ControllerBase
{
    private readonly ICandleSummaryService _candleSummaryService;

    public CandleSummaryController(ICandleSummaryService candleSummaryService)
    {
        _candleSummaryService = candleSummaryService ?? throw new ArgumentNullException(nameof(candleSummaryService));
    }

    /// <summary>
    /// Retrieves timeframe-wise total stock candle summary report with pagination and filters.
    /// Strictly accessible by Admin role users.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string symbol = "ALL",
        [FromQuery] string timeframe = "ALL",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var filter = new CandleSummaryFilterDto
        {
            FromDate = fromDate,
            ToDate = toDate,
            Symbol = symbol,
            Timeframe = timeframe,
            Page = page,
            PageSize = pageSize
        };

        var response = await _candleSummaryService.GetCandleSummaryAsync(filter);
        return Ok(response);
    }

    /// <summary>
    /// Retrieves list of active stock symbols for dropdown filtering.
    /// </summary>
    [HttpGet("symbols")]
    public async Task<IActionResult> GetSymbols()
    {
        var symbols = await _candleSummaryService.GetActiveSymbolsAsync();
        return Ok(symbols);
    }
}
