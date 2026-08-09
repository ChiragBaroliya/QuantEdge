using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class PaperMatchingEngine
{
    private readonly IPaperTradingRepository _repository;
    private readonly ILogger<PaperMatchingEngine> _logger;
    private readonly ConcurrentDictionary<string, decimal> _latestPrices = new(StringComparer.OrdinalIgnoreCase);

    public PaperMatchingEngine(
        IPaperTradingRepository repository,
        ILogger<PaperMatchingEngine> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void UpdateLtp(string symbol, decimal ltp)
    {
        if (!string.IsNullOrWhiteSpace(symbol) && ltp > 0m)
        {
            _latestPrices[symbol] = ltp;
        }
    }

    public decimal GetLtp(string symbol)
    {
        return _latestPrices.TryGetValue(symbol, out var price) ? price : 0m;
    }

    public async Task ProcessTickAsync(string symbol, decimal ltp)
    {
        if (string.IsNullOrWhiteSpace(symbol) || ltp <= 0m) return;
        UpdateLtp(symbol, ltp);

        var account = await _repository.GetAccountAsync("default_user");
        if (account == null) return;

        // 1. Process Open Positions for Stop-Loss & Take-Profit Auto-Triggers
        var openPositions = await _repository.GetPositionsAsync(account.Id, openOnly: true);
        var matchingPositions = openPositions.Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

        foreach (var pos in matchingPositions)
        {
            // Update current price & unrealized PnL
            decimal unrealizedPnl = pos.Side == TradeSide.BUY
                ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                : (pos.AverageEntryPrice - ltp) * pos.Quantity;

            pos.CurrentPrice = ltp;
            pos.UnrealizedPnl = unrealizedPnl;
            await _repository.UpsertPositionAsync(pos);

            // Check Stop-Loss Trigger
            bool slTriggered = false;
            if (pos.StopLoss.HasValue && pos.StopLoss.Value > 0m)
            {
                if (pos.Side == TradeSide.BUY && ltp <= pos.StopLoss.Value) slTriggered = true;
                if (pos.Side == TradeSide.SELL && ltp >= pos.StopLoss.Value) slTriggered = true;
            }

            // Check Take-Profit Trigger
            bool tpTriggered = false;
            if (pos.TakeProfit.HasValue && pos.TakeProfit.Value > 0m)
            {
                if (pos.Side == TradeSide.BUY && ltp >= pos.TakeProfit.Value) tpTriggered = true;
                if (pos.Side == TradeSide.SELL && ltp <= pos.TakeProfit.Value) tpTriggered = true;
            }

            if (slTriggered || tpTriggered)
            {
                string reason = slTriggered ? "Stop-Loss Triggered" : "Take-Profit Triggered";
                _logger.LogInformation("Auto-closing position {PositionId} for {Symbol} at {Ltp}. Reason: {Reason}", pos.Id, symbol, ltp, reason);

                decimal realizedPnl = pos.Side == TradeSide.BUY
                    ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                    : (pos.AverageEntryPrice - ltp) * pos.Quantity;

                await _repository.ClosePositionAsync(pos.Id, ltp, realizedPnl);

                // Release used margin & update account balance
                decimal releasedMargin = pos.Quantity * pos.AverageEntryPrice;
                decimal updatedBalance = account.CurrentBalance + realizedPnl;
                decimal updatedUsedMargin = Math.Max(0m, account.UsedMargin - releasedMargin);
                decimal updatedRealizedPnl = account.RealizedPnl + realizedPnl;

                await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, updatedBalance, updatedUsedMargin, updatedRealizedPnl);

                // Record Trade History
                await _repository.RecordTradeHistoryAsync(new PaperTradeHistory
                {
                    AccountId = account.Id,
                    OrderId = 0,
                    Symbol = symbol,
                    Side = pos.Side == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY,
                    Quantity = pos.Quantity,
                    ExecutedPrice = ltp,
                    RealizedPnl = realizedPnl,
                    Remarks = $"Auto-Exit: {reason}"
                });
            }
        }

        // 2. Process Pending Limit Orders
        var activeOrders = await _repository.GetOrdersAsync(account.Id, activeOnly: true);
        var pendingLimitOrders = activeOrders.Where(o => 
            string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && 
            o.OrderType == PaperOrderType.Limit &&
            o.Status == PaperOrderStatus.Pending);

        foreach (var order in pendingLimitOrders)
        {
            bool match = false;
            if (order.Side == TradeSide.BUY && ltp <= order.Price) match = true;
            if (order.Side == TradeSide.SELL && ltp >= order.Price) match = true;

            if (match)
            {
                _logger.LogInformation("Filling Pending Limit Order {OrderId} for {Symbol} at {Ltp}", order.Id, symbol, ltp);
                await _repository.UpdateOrderStatusAsync(order.Id, PaperOrderStatus.Filled, ltp, "Limit Order Filled");

                // Execute position update
                var existingPos = await _repository.GetOpenPositionBySymbolAsync(account.Id, symbol);
                if (existingPos == null)
                {
                    await _repository.UpsertPositionAsync(new PaperPosition
                    {
                        AccountId = account.Id,
                        Symbol = symbol,
                        Side = order.Side,
                        Quantity = order.Quantity,
                        AverageEntryPrice = ltp,
                        CurrentPrice = ltp,
                        UnrealizedPnl = 0m,
                        StopLoss = order.StopLoss,
                        TakeProfit = order.TakeProfit,
                        Status = PositionStatus.OPEN,
                        RealizedPnl = 0m
                    });
                }
                else if (existingPos.Side == order.Side)
                {
                    int newQty = existingPos.Quantity + order.Quantity;
                    decimal avgPrice = ((existingPos.Quantity * existingPos.AverageEntryPrice) + (order.Quantity * ltp)) / newQty;
                    existingPos.Quantity = newQty;
                    existingPos.AverageEntryPrice = avgPrice;
                    existingPos.CurrentPrice = ltp;
                    await _repository.UpsertPositionAsync(existingPos);
                }

                // Record Trade History
                await _repository.RecordTradeHistoryAsync(new PaperTradeHistory
                {
                    AccountId = account.Id,
                    OrderId = order.Id,
                    Symbol = symbol,
                    Side = order.Side,
                    Quantity = order.Quantity,
                    ExecutedPrice = ltp,
                    RealizedPnl = 0m,
                    Remarks = "Limit Order Executed"
                });
            }
        }
    }
}
