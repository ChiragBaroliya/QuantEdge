using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Thread-safe in-memory order matching and position monitoring engine.
/// Caches active orders and positions to prevent database connection pool exhaustion on high-frequency WebSocket ticks.
/// </summary>
public class PaperMatchingEngine
{
    private readonly IPaperTradingRepository _repository;
    private readonly ILogger<PaperMatchingEngine> _logger;
    private readonly ConcurrentDictionary<string, decimal> _latestPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _closingPositions = new();

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private PaperAccount? _cachedAccount;
    private List<PaperPosition> _cachedOpenPositions = new();
    private List<PaperOrder> _cachedPendingLimitOrders = new();
    private HashSet<string> _monitoredSymbols = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Force invalidates the matching engine's in-memory cache when new orders or positions are modified.
    /// </summary>
    public void InvalidateCache()
    {
        _lastCacheRefresh = DateTime.MinValue;
    }

    public async Task ProcessTickAsync(string symbol, decimal ltp)
    {
        if (string.IsNullOrWhiteSpace(symbol) || ltp <= 0m) return;
        UpdateLtp(symbol, ltp);

        // Ensure in-memory active orders & positions cache is fresh (< 5s old)
        await EnsureCacheFreshAsync();

        // High-frequency fast path: If this symbol has no active open positions or pending limit orders, exit immediately (< 0.01 ms, 0 DB queries)
        if (!_monitoredSymbols.Contains(symbol))
        {
            return;
        }

        var account = _cachedAccount;
        if (account == null) return;

        // 1. Process Open Positions for Stop-Loss & Take-Profit Auto-Triggers
        var matchingPositions = _cachedOpenPositions
            .Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var pos in matchingPositions)
        {
            if (pos.Status == PositionStatus.CLOSED || _closingPositions.ContainsKey(pos.Id))
            {
                continue;
            }

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
                // Atomic Gate: Only ONE concurrent thread can ever close this position ID
                if (!_closingPositions.TryAdd(pos.Id, 0))
                {
                    continue;
                }

                pos.Status = PositionStatus.CLOSED;

                string reason = slTriggered ? "Stop-Loss Triggered" : "Take-Profit Triggered";
                _logger.LogInformation("Auto-closing position {PositionId} for {Symbol} at {Ltp}. Reason: {Reason}", pos.Id, symbol, ltp, reason);

                decimal realizedPnl = pos.Side == TradeSide.BUY
                    ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                    : (pos.AverageEntryPrice - ltp) * pos.Quantity;

                bool closedSuccessfully = await _repository.ClosePositionAsync(pos.Id, ltp, realizedPnl, reason);
                if (!closedSuccessfully)
                {
                    _logger.LogWarning("Position {PositionId} was already closed. Skipping duplicate history logging.", pos.Id);
                    continue;
                }

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
                    EntryPrice = pos.AverageEntryPrice,
                    ExecutedPrice = ltp,
                    RealizedPnl = realizedPnl,
                    TradeType = pos.TradeType,
                    ExitReason = reason,
                    Remarks = $"Auto-Exit: {reason}"
                });

                _cachedOpenPositions.RemoveAll(p => p.Id == pos.Id);
                InvalidateCache();
            }
            else
            {
                // Update current price & unrealized PnL only when NOT closing
                decimal unrealizedPnl = pos.Side == TradeSide.BUY
                    ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                    : (pos.AverageEntryPrice - ltp) * pos.Quantity;

                pos.CurrentPrice = ltp;
                pos.UnrealizedPnl = unrealizedPnl;
                await _repository.UpsertPositionAsync(pos);
            }
        }

        // 2. Process Pending Limit Orders
        var pendingLimitOrders = _cachedPendingLimitOrders
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && 
                        o.OrderType == PaperOrderType.Limit && 
                        o.Status == PaperOrderStatus.Pending)
            .ToList();

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
                    EntryPrice = ltp,
                    ExecutedPrice = ltp,
                    RealizedPnl = 0m,
                    Remarks = "Limit Order Executed"
                });

                InvalidateCache();
            }
        }
    }

    private async Task EnsureCacheFreshAsync()
    {
        if (DateTime.UtcNow - _lastCacheRefresh < CacheTtl && _cachedAccount != null)
        {
            return;
        }

        await _cacheLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _lastCacheRefresh < CacheTtl && _cachedAccount != null)
            {
                return;
            }

            var account = await _repository.GetAccountAsync("default_user");
            if (account != null)
            {
                var positions = (await _repository.GetPositionsAsync(account.Id, openOnly: true)).ToList();
                var orders = (await _repository.GetOrdersAsync(account.Id, activeOnly: true))
                    .Where(o => o.OrderType == PaperOrderType.Limit && o.Status == PaperOrderStatus.Pending)
                    .ToList();

                var newMonitored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in positions) newMonitored.Add(p.Symbol);
                foreach (var o in orders) newMonitored.Add(o.Symbol);

                _cachedAccount = account;
                _cachedOpenPositions = positions;
                _cachedPendingLimitOrders = orders;
                _monitoredSymbols = newMonitored;
                _lastCacheRefresh = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh PaperMatchingEngine in-memory cache from database.");
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}

