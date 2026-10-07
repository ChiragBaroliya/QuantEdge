using System;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>Result of closing all or part of a paper position.</summary>
public sealed record PaperCloseResult(int ClosedQuantity, int RemainingQuantity, decimal RealizedPnl, bool IsFullClose);

/// <summary>
/// The one place a paper position is closed by an opposite-side order (market or filled limit), so every path
/// books P&L the same way: realized P&L on the closed quantity only, the position reduced or closed, margin
/// released proportionally, and the trade-history row (what Reports read) carrying that P&L.
/// </summary>
public static class PaperPositionCloser
{
    /// <param name="extraMarginToRelease">Margin already blocked for this order itself (a closing limit order blocks margin when placed).</param>
    public static async Task<PaperCloseResult> CloseAsync(
        IPaperTradingRepository repository,
        PaperAccount account,
        PaperPosition position,
        int orderQuantity,
        decimal price,
        int orderId,
        string exitReason,
        decimal extraMarginToRelease = 0m)
    {
        int closeQty = Math.Min(orderQuantity, position.Quantity);
        decimal realizedPnl = position.Side == TradeSide.BUY
            ? (price - position.AverageEntryPrice) * closeQty
            : (position.AverageEntryPrice - price) * closeQty;
        decimal entryPrice = position.AverageEntryPrice;
        bool isFullClose = closeQty == position.Quantity;

        if (isFullClose)
        {
            // Position row keeps the trade's total realized P&L (earlier partial closes + this one).
            await repository.ClosePositionAsync(position.Id, price, position.RealizedPnl + realizedPnl, exitReason);
        }
        else
        {
            position.Quantity -= closeQty;
            position.RealizedPnl += realizedPnl;
            position.CurrentPrice = price;
            position.UnrealizedPnl = position.Side == TradeSide.BUY
                ? (price - position.AverageEntryPrice) * position.Quantity
                : (position.AverageEntryPrice - price) * position.Quantity;
            await repository.UpsertPositionAsync(position);
        }

        decimal releasedMargin = closeQty * entryPrice + extraMarginToRelease;
        await repository.UpdateAccountBalanceAndMarginAsync(account.Id,
            account.CurrentBalance + realizedPnl,
            Math.Max(0m, account.UsedMargin - releasedMargin),
            account.RealizedPnl + realizedPnl);

        await repository.RecordTradeHistoryAsync(new PaperTradeHistory
        {
            AccountId = account.Id,
            OrderId = orderId,
            Symbol = position.Symbol,
            Side = position.Side == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY,
            Quantity = closeQty,
            EntryPrice = entryPrice,
            ExecutedPrice = price,
            RealizedPnl = realizedPnl,
            TradeType = position.TradeType,
            ExitReason = isFullClose ? exitReason : $"{exitReason} (partial)",
            Remarks = isFullClose
                ? $"{exitReason} - position closed"
                : $"{exitReason} - partial close, {position.Quantity} remaining"
        });

        return new PaperCloseResult(closeQty, isFullClose ? 0 : position.Quantity, realizedPnl, isFullClose);
    }
}
