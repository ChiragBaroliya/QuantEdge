namespace QuantEdge.Infrastructure.Models;

/// <summary>
/// Represents a single price level in the order book (market depth).
/// </summary>
public record DepthLevel(
    decimal Price,
    long Quantity
);
