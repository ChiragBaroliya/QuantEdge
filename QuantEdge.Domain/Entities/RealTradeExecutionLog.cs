using System;

namespace QuantEdge.Domain.Entities;

public class RealTradeExecutionLog
{
    public int Id { get; set; }
    public int UserId { get; set; } = 1;
    public string Symbol { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public int? Quantity { get; set; }
    public string? Reason { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}
