using System;

namespace QuantEdge.Infrastructure.Services;

public enum RealFillOutcome
{
    /// <summary>Still resting at the broker, or COMPLETE without an average price yet - settle on a later reconcile pass.</summary>
    Pending,
    /// <summary>Fully executed (COMPLETE) with the broker's average price.</summary>
    Filled,
    /// <summary>Ended CANCELLED/REJECTED after part of it traded - shares DID change hands for FilledQuantity.</summary>
    PartiallyFilledThenClosed,
    /// <summary>Ended REJECTED/CANCELLED with nothing traded.</summary>
    Rejected
}

/// <summary>What actually traded, taken only from the broker - never from the live price or the order's own price.</summary>
public sealed record RealFill(RealFillOutcome Outcome, int Quantity, decimal Price);

/// <summary>
/// Turns one Kite order-status reading (status, average_price, filled_quantity from /orders/{id}) into what
/// really happened. Pure function on data the status call already returns - it adds no Kite calls.
///
/// Rules:
///  - COMPLETE + average_price > 0 -> Filled at average_price for filled_quantity (ordered qty if Kite sends 0).
///  - COMPLETE + no average_price  -> Pending (was: fall back to LTP / order price - an invented fill price).
///  - CANCELLED / REJECTED with filled_quantity > 0 -> PartiallyFilledThenClosed (was: treated as "nothing happened",
///    leaving bought shares unmonitored or sold shares still shown as held). Pending until the average price arrives.
///  - CANCELLED / REJECTED with nothing filled -> Rejected.
///  - anything else (OPEN, TRIGGER PENDING, MODIFY..., unknown) -> Pending.
/// </summary>
public static class RealFillResolver
{
    public static RealFill Resolve(string? brokerStatus, decimal averagePrice, int filledQuantity, int orderedQuantity)
    {
        bool isComplete = string.Equals(brokerStatus, "COMPLETE", StringComparison.OrdinalIgnoreCase);
        bool isClosedWithoutComplete =
            string.Equals(brokerStatus, "CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(brokerStatus, "REJECTED", StringComparison.OrdinalIgnoreCase);

        if (isComplete)
        {
            if (averagePrice <= 0m) return new RealFill(RealFillOutcome.Pending, 0, 0m);
            int qty = filledQuantity > 0 ? filledQuantity : orderedQuantity;
            return new RealFill(RealFillOutcome.Filled, qty, averagePrice);
        }

        if (isClosedWithoutComplete)
        {
            if (filledQuantity <= 0) return new RealFill(RealFillOutcome.Rejected, 0, 0m);
            return averagePrice > 0m
                ? new RealFill(RealFillOutcome.PartiallyFilledThenClosed, Math.Min(filledQuantity, orderedQuantity), averagePrice)
                : new RealFill(RealFillOutcome.Pending, 0, 0m);
        }

        return new RealFill(RealFillOutcome.Pending, 0, 0m);
    }
}
