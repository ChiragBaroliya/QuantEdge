using System;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Manual Paper Trading execution log (manual_paper_trade_execution_logs) - separate from
/// AutoTradeExecutionLog so manual activity never appears in, or counts toward, Auto Paper Trading.
/// </summary>
public class ManualPaperTradeExecutionLog
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public string Symbol { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty; // MANUAL_BUY, MANUAL_SELL, TRADE_SKIPPED, CIRCUIT_BREAKER, TRAILING_SL_ACTIVATED, SYSTEM_ERROR
    public decimal? Price { get; set; }
    public int? Quantity { get; set; }
    public string? Reason { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}
