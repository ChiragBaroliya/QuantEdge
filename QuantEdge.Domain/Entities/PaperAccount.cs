using System;

namespace QuantEdge.Domain.Entities;

public class PaperAccount
{
    public int Id { get; set; }
    public string UserId { get; set; } = "default_user";
    public string AccountName { get; set; } = "Virtual Trading Account";
    public decimal InitialBalance { get; set; } = 100000m;
    public decimal CurrentBalance { get; set; } = 100000m;
    public decimal UsedMargin { get; set; } = 0m;
    public decimal AvailableMargin => CurrentBalance - UsedMargin;
    public decimal RealizedPnl { get; set; } = 0m;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
