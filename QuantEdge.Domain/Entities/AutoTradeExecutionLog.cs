using System;

namespace QuantEdge.Domain.Entities;

public class AutoTradeExecutionLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default_user";
    public string Symbol { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty; // AUTO_BUY, AUTO_SELL, SIGNAL_SKIPPED, SYSTEM_ALERT, TOKEN_EXPIRED
    public decimal? Price { get; set; }
    public int? Quantity { get; set; }
    public string? Reason { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}
