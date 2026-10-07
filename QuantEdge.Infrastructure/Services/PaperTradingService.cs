using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Domain.Exceptions;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Hubs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class PaperTradingService : IPaperTradingService
{
    private readonly IPaperTradingRepository _repository;
    private readonly PaperOrderValidator _validator;
    private readonly PaperMatchingEngine _matchingEngine;
    private readonly IHubContext<MarketDataHub>? _hubContext;
    private readonly ILogger<PaperTradingService> _logger;
    private static bool _autoTradeEnabled = false;

    public PaperTradingService(
        IPaperTradingRepository repository,
        PaperOrderValidator validator,
        PaperMatchingEngine matchingEngine,
        ILogger<PaperTradingService> logger,
        IHubContext<MarketDataHub>? hubContext = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _matchingEngine = matchingEngine ?? throw new ArgumentNullException(nameof(matchingEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hubContext = hubContext;
    }

    public async Task<PaperAccount> GetOrCreateAccountAsync(string userId = "default_user")
    {
        var account = await _repository.GetAccountAsync(userId);
        if (account == null)
        {
            account = await _repository.CreateAccountAsync(userId, "Virtual Trading Account", 100000m);
        }
        return account;
    }

    public async Task<PaperPortfolioDto> GetPortfolioAsync(string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        var openPositions = await _repository.GetPositionsAsync(account.Id, openOnly: true);

        // Recalculate unrealized PnL based on latest prices
        decimal totalUnrealizedPnl = 0m;
        foreach (var pos in openPositions)
        {
            decimal ltp = _matchingEngine.GetLtp(pos.Symbol);
            pos.PriceSource = ltp > 0m ? "LIVE" : pos.CurrentPrice > 0m ? "STORED" : "NONE";
            pos.PriceAsOfUtc = ltp > 0m ? DateTime.UtcNow : null;
            if (ltp <= 0m) ltp = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.AverageEntryPrice;

            decimal pnl = pos.Side == TradeSide.BUY
                ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                : (pos.AverageEntryPrice - ltp) * pos.Quantity;

            pos.CurrentPrice = ltp;
            pos.UnrealizedPnl = pnl;
            pos.EstimatedCharges = EstimateCharges(pos);
            totalUnrealizedPnl += pnl;
        }

        return new PaperPortfolioDto
        {
            Account = account,
            TotalUnrealizedPnl = totalUnrealizedPnl,
            TotalEstimatedCharges = Math.Round(openPositions.Sum(p => p.EstimatedCharges), 2),
            AutoTradeEnabled = _autoTradeEnabled
        };
    }

    public async Task<PaperOrder> PlaceOrderAsync(PlacePaperOrderDto dto, string userId = "default_user", decimal currentLtp = 0m)
    {
        var account = await GetOrCreateAccountAsync(userId);

        if (currentLtp <= 0m)
        {
            currentLtp = _matchingEngine.GetLtp(dto.Symbol);
        }

        // Validate order parameters
        await _validator.ValidateOrderPlacementAsync(dto, account, currentLtp);

        decimal executionPrice = dto.OrderType == PaperOrderType.Market ? currentLtp : dto.Price;
        decimal requiredMargin = dto.Quantity * executionPrice;

        // A market order against an open position in the opposite direction closes (part of) it. Selling more
        // than is held would need a reversal into a short, which paper trading doesn't model - reject it
        // instead of silently dropping the excess quantity.
        var existingPosition = dto.OrderType == PaperOrderType.Market
            ? await _repository.GetOpenPositionBySymbolAsync(account.Id, dto.Symbol)
            : null;
        bool isClosingOrder = existingPosition != null && existingPosition.Side != dto.Side;
        if (isClosingOrder && dto.Quantity > existingPosition!.Quantity)
        {
            throw new InvalidOrderException(
                $"Quantity {dto.Quantity} exceeds the open position of {existingPosition.Quantity} {existingPosition.Symbol}. Close at most {existingPosition.Quantity}.",
                "QUANTITY_EXCEEDS_POSITION");
        }

        var newOrder = new PaperOrder
        {
            AccountId = account.Id,
            Symbol = dto.Symbol.ToUpper().Trim(),
            OrderType = dto.OrderType,
            Side = dto.Side,
            Quantity = dto.Quantity,
            Price = executionPrice,
            StopLoss = dto.StopLoss,
            TakeProfit = dto.TakeProfit,
            Status = dto.OrderType == PaperOrderType.Market ? PaperOrderStatus.Filled : PaperOrderStatus.Pending,
            FilledPrice = dto.OrderType == PaperOrderType.Market ? executionPrice : null,
            FilledAt = dto.OrderType == PaperOrderType.Market ? DateTime.UtcNow : null,
            Remarks = dto.OrderType == PaperOrderType.Market ? "Executed at Market LTP" : "Pending Limit Trigger"
        };

        var createdOrder = await _repository.CreateOrderAsync(newOrder);

        // Block margin for orders that open or add to a position (a closing order releases margin below instead).
        if (!isClosingOrder)
        {
            await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, account.CurrentBalance, account.UsedMargin + requiredMargin, account.RealizedPnl);
        }

        // If Market Order, update or open position immediately
        if (dto.OrderType == PaperOrderType.Market)
        {
            if (isClosingOrder)
            {
                // Opposite direction: full or partial close (quantity validated above). Books the P&L into the
                // history row too - Reports read history, so a 0 there used to drop the trade's profit.
                await PaperPositionCloser.CloseAsync(_repository, account, existingPosition!, dto.Quantity, executionPrice,
                    createdOrder.Id, "Manual Close");
            }
            else
            {
                if (existingPosition == null)
                {
                    await _repository.UpsertPositionAsync(new PaperPosition
                    {
                        AccountId = account.Id,
                        Symbol = dto.Symbol.ToUpper().Trim(),
                        Side = dto.Side,
                        Quantity = dto.Quantity,
                        AverageEntryPrice = executionPrice,
                        CurrentPrice = executionPrice,
                        UnrealizedPnl = 0m,
                        StopLoss = dto.StopLoss,
                        TakeProfit = dto.TakeProfit,
                        Status = PositionStatus.OPEN,
                        RealizedPnl = 0m
                    });
                }
                else
                {
                    int totalQty = existingPosition.Quantity + dto.Quantity;
                    decimal avgPrice = ((existingPosition.Quantity * existingPosition.AverageEntryPrice) + (dto.Quantity * executionPrice)) / totalQty;
                    existingPosition.Quantity = totalQty;
                    existingPosition.AverageEntryPrice = avgPrice;
                    existingPosition.CurrentPrice = executionPrice;
                    existingPosition.StopLoss = dto.StopLoss ?? existingPosition.StopLoss;
                    existingPosition.TakeProfit = dto.TakeProfit ?? existingPosition.TakeProfit;
                    await _repository.UpsertPositionAsync(existingPosition);
                }

                // Record Trade History
                await _repository.RecordTradeHistoryAsync(new PaperTradeHistory
                {
                    AccountId = account.Id,
                    OrderId = createdOrder.Id,
                    Symbol = dto.Symbol.ToUpper().Trim(),
                    Side = dto.Side,
                    Quantity = dto.Quantity,
                    EntryPrice = executionPrice,
                    ExecutedPrice = executionPrice,
                    RealizedPnl = 0m,
                    TradeType = existingPosition?.TradeType ?? TradeType.Manual,
                    Remarks = "Market Order Executed"
                });
            }
        }

        // Invalidate matching engine cache so new order is monitored immediately
        _matchingEngine.InvalidateCache();

        // Broadcast real-time portfolio update via SignalR
        await BroadcastPortfolioUpdateAsync(userId);
        return createdOrder;
    }

    public async Task CancelOrderAsync(int orderId, string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        var order = await _repository.GetOrderByIdAsync(orderId);

        if (order == null || order.AccountId != account.Id)
        {
            throw new OrderNotFoundException(orderId);
        }

        if (order.Status != PaperOrderStatus.Pending)
        {
            throw new InvalidOrderException($"Order #{orderId} cannot be cancelled because it is not in Pending status.", "ORDER_CANCEL_FAILED");
        }

        await _repository.UpdateOrderStatusAsync(orderId, PaperOrderStatus.Cancelled, remarks: "Cancelled by user");

        // Release order margin
        decimal releasedMargin = order.Quantity * order.Price;
        decimal newUsedMargin = Math.Max(0m, account.UsedMargin - releasedMargin);
        await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, account.CurrentBalance, newUsedMargin, account.RealizedPnl);

        _matchingEngine.InvalidateCache();
        await BroadcastPortfolioUpdateAsync(userId);
    }

    public async Task ClosePositionAsync(int positionId, decimal currentLtp = 0m, string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        var openPositions = await _repository.GetPositionsAsync(account.Id, openOnly: true);
        var position = openPositions.FirstOrDefault(p => p.Id == positionId);

        if (position == null)
        {
            throw new PositionNotFoundException(positionId);
        }

        if (currentLtp <= 0m)
        {
            currentLtp = _matchingEngine.GetLtp(position.Symbol);
            if (currentLtp <= 0m) currentLtp = position.CurrentPrice > 0m ? position.CurrentPrice : position.AverageEntryPrice;
        }

        decimal realizedPnl = position.Side == TradeSide.BUY
            ? (currentLtp - position.AverageEntryPrice) * position.Quantity
            : (position.AverageEntryPrice - currentLtp) * position.Quantity;

        bool closedSuccessfully = await _repository.ClosePositionAsync(positionId, currentLtp, realizedPnl);
        if (!closedSuccessfully)
        {
            return;
        }

        decimal releasedMargin = position.Quantity * position.AverageEntryPrice;
        decimal updatedBalance = account.CurrentBalance + realizedPnl;
        decimal updatedUsedMargin = Math.Max(0m, account.UsedMargin - releasedMargin);
        decimal updatedRealizedPnl = account.RealizedPnl + realizedPnl;

        await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, updatedBalance, updatedUsedMargin, updatedRealizedPnl);

        await _repository.RecordTradeHistoryAsync(new PaperTradeHistory
        {
            AccountId = account.Id,
            OrderId = 0,
            Symbol = position.Symbol,
            Side = position.Side == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY,
            Quantity = position.Quantity,
            EntryPrice = position.AverageEntryPrice,
            ExecutedPrice = currentLtp,
            RealizedPnl = realizedPnl,
            TradeType = position.TradeType,
            ExitReason = "Manual Close",
            Remarks = "Manual Position Exit"
        });

        _matchingEngine.InvalidateCache();
        await BroadcastPortfolioUpdateAsync(userId);
    }

    public async Task ResetAccountAsync(string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        await _repository.ResetAccountAsync(account.Id, 100000m);
        _matchingEngine.InvalidateCache();
        await BroadcastPortfolioUpdateAsync(userId);
    }


    public async Task ProcessTickForPaperMatchingAsync(string symbol, decimal ltp)
    {
        await _matchingEngine.ProcessTickAsync(symbol, ltp);
    }

    public async Task ProcessSignalForAutoTradeAsync(TradingSignal signal)
    {
        if (signal == null || string.IsNullOrWhiteSpace(signal.Symbol)) return;

        var account = await GetOrCreateAccountAsync("default_user");
        if (!account.IsAutoTradeEnabled) return;

        // 1. Timeframe Matching Check (e.g. signal timeframe must match user setting or default 1m)
        string targetTf = string.IsNullOrWhiteSpace(account.AutoTradeTimeframe) ? "1m" : account.AutoTradeTimeframe;
        if (!string.Equals(signal.Timeframe, targetTf, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 2. Signal Strength Threshold Check (e.g. >= 70%)
        decimal minStrength = account.AutoTradeMinSignalStrength > 0 ? account.AutoTradeMinSignalStrength : 70m;
        if (signal.SignalStrength < minStrength)
        {
            _logger.LogInformation("Auto-trade ignored for {Symbol}: Strength {Strength}% below threshold {MinStrength}%",
                signal.Symbol, signal.SignalStrength, minStrength);
            return;
        }

        // 3. Trade Side Evaluation
        if (!string.Equals(signal.SignalType, "BUY", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(signal.SignalType, "SELL", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TradeSide newSide = string.Equals(signal.SignalType, "BUY", StringComparison.OrdinalIgnoreCase) ? TradeSide.BUY : TradeSide.SELL;
        int tradeQty = account.AutoTradeQuantity > 0 ? account.AutoTradeQuantity : 25;

        // 4. Position Reversal & Max Open Position Checks
        var openPositions = (await _repository.GetPositionsAsync(account.Id, openOnly: true)).ToList();
        var existingPosition = openPositions.FirstOrDefault(p => string.Equals(p.Symbol, signal.Symbol, StringComparison.OrdinalIgnoreCase));

        if (existingPosition != null)
        {
            if (existingPosition.Side == newSide)
            {
                _logger.LogInformation("Auto-trade skipped for {Symbol}: Position already open in same direction ({Side})", signal.Symbol, newSide);
                return;
            }

            // Signal Reversal: Close opposite position first!
            _logger.LogInformation("Auto-trade position reversal for {Symbol}: Closing {OldSide} position before opening {NewSide}",
                signal.Symbol, existingPosition.Side, newSide);
            await ClosePositionAsync(existingPosition.Id, signal.EntryPrice, "default_user");
        }
        else if (openPositions.Count >= account.MaxOpenPositions)
        {
            _logger.LogWarning("Auto-trade rejected for {Symbol}: Reached max open positions limit ({Limit})", signal.Symbol, account.MaxOpenPositions);
            return;
        }

        // 5. SL / TP Calculation
        decimal slPct = account.AutoTradeStopLossPercent > 0 ? account.AutoTradeStopLossPercent / 100m : 0.01m;
        decimal tpPct = account.AutoTradeTakeProfitPercent > 0 ? account.AutoTradeTakeProfitPercent / 100m : 0.02m;

        decimal stopLoss = newSide == TradeSide.BUY ? signal.EntryPrice * (1m - slPct) : signal.EntryPrice * (1m + slPct);
        decimal takeProfit = newSide == TradeSide.BUY ? signal.EntryPrice * (1m + tpPct) : signal.EntryPrice * (1m - tpPct);

        try
        {
            var placedOrder = await PlaceOrderAsync(new PlacePaperOrderDto
            {
                Symbol = signal.Symbol,
                Side = newSide,
                OrderType = PaperOrderType.Market,
                Quantity = tradeQty,
                StopLoss = stopLoss,
                TakeProfit = takeProfit
            }, "default_user", signal.EntryPrice);

            // Broadcast Toast Notification to Frontend via SignalR
            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveAutoTradeAlert", new {
                    symbol = signal.Symbol,
                    side = signal.SignalType,
                    quantity = tradeQty,
                    price = signal.EntryPrice,
                    mode = account.TradingMode ?? "Paper",
                    strength = signal.SignalStrength,
                    message = $"⚡ Auto-Trade Executed ({account.TradingMode}): {signal.SignalType} {tradeQty} shares of {signal.Symbol} @ ₹{signal.EntryPrice:N2}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-trade execution failed for signal on {Symbol}", signal.Symbol);
        }
    }

    public async Task SetAutoTradeStatusAsync(bool enabled)
    {
        var settings = await GetAutoTradeSettingsAsync();
        settings.IsAutoTradeEnabled = enabled;
        await UpdateAutoTradeSettingsAsync(settings);
    }

    public bool GetAutoTradeStatus()
    {
        var account = _repository.GetAccountAsync("default_user").GetAwaiter().GetResult();
        return account?.IsAutoTradeEnabled ?? _autoTradeEnabled;
    }

    public async Task<AutoTradeSettingsDto> GetAutoTradeSettingsAsync(string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        return new AutoTradeSettingsDto
        {
            IsAutoTradeEnabled = account.IsAutoTradeEnabled,
            TradingMode = account.TradingMode ?? "Paper",
            AutoTradeTimeframe = account.AutoTradeTimeframe ?? "1m",
            AutoTradeMinSignalStrength = account.AutoTradeMinSignalStrength <= 0 ? 70m : account.AutoTradeMinSignalStrength,
            AutoTradeQuantity = account.AutoTradeQuantity <= 0 ? 25 : account.AutoTradeQuantity,
            AutoTradeStopLossPercent = account.AutoTradeStopLossPercent <= 0 ? 1.0m : account.AutoTradeStopLossPercent,
            AutoTradeTakeProfitPercent = account.AutoTradeTakeProfitPercent <= 0 ? 2.0m : account.AutoTradeTakeProfitPercent,
            MaxOpenPositions = account.MaxOpenPositions <= 0 ? 5 : account.MaxOpenPositions,
            DailyMaxLossLimit = account.DailyMaxLossLimit <= 0 ? 2000m : account.DailyMaxLossLimit
        };
    }

    public async Task<AutoTradeSettingsDto> UpdateAutoTradeSettingsAsync(AutoTradeSettingsDto settings, string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        await _repository.UpdateAutoTradeSettingsAsync(account.Id, settings);
        _autoTradeEnabled = settings.IsAutoTradeEnabled;
        await BroadcastPortfolioUpdateAsync(userId);
        return settings;
    }

    public async Task<IEnumerable<PaperPosition>> GetOpenPositionsAsync(string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        var positions = await _repository.GetPositionsAsync(account.Id, openOnly: true);
        foreach (var pos in positions)
        {
            decimal ltp = _matchingEngine.GetLtp(pos.Symbol);
            if (ltp > 0m)
            {
                pos.CurrentPrice = ltp;
                pos.UnrealizedPnl = pos.Side == TradeSide.BUY
                    ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                    : (pos.AverageEntryPrice - ltp) * pos.Quantity;
                pos.PriceSource = "LIVE";
                pos.PriceAsOfUtc = DateTime.UtcNow;
            }
            else
            {
                // No tick in this process: the stored current_price (written by the feed worker) - age unknown.
                pos.PriceSource = pos.CurrentPrice > 0m ? "STORED" : "NONE";
            }
            pos.EstimatedCharges = EstimateCharges(pos);
        }
        return positions;
    }

    // "Net if sold now" for a paper position: round-trip charges at its current price (display only).
    internal static decimal EstimateCharges(PaperPosition pos) =>
        Math.Round(ChargesCalculator.EstimateOpenPosition(pos.AverageEntryPrice, pos.CurrentPrice, pos.Quantity,
            pos.Side == TradeSide.SELL, pos.OpenedAt), 2);

    public async Task<IEnumerable<PaperOrder>> GetOrdersAsync(string userId = "default_user", bool activeOnly = false)
    {
        var account = await GetOrCreateAccountAsync(userId);
        return await _repository.GetOrdersAsync(account.Id, activeOnly);
    }

    public async Task<IEnumerable<PaperTradeHistory>> GetTradeHistoryAsync(string userId = "default_user", int limit = 50)
    {
        var account = await GetOrCreateAccountAsync(userId);
        return await _repository.GetTradeHistoryAsync(account.Id, limit);
    }

    public async Task<PagedResultDto<PaperTradeHistory>> GetTradeHistoryPagedAsync(PaperTradeHistoryFilterDto filter, string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        var (items, totalCount) = await _repository.GetTradeHistoryPagedAsync(account.Id, filter);

        return new PagedResultDto<PaperTradeHistory>
        {
            Items = items,
            TotalCount = totalCount,
            Page = filter.Page < 1 ? 1 : filter.Page,
            PageSize = filter.PageSize <= 0 ? 10 : filter.PageSize
        };
    }

    private async Task BroadcastPortfolioUpdateAsync(string userId)
    {
        try
        {
            if (_hubContext != null)
            {
                var portfolio = await GetPortfolioAsync(userId);
                var positions = await GetOpenPositionsAsync(userId);
                await _hubContext.Clients.All.SendAsync("ReceivePaperAccountUpdate", portfolio);
                await _hubContext.Clients.All.SendAsync("ReceivePaperPositionsUpdate", positions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast Paper Trading updates via SignalR.");
        }
    }
}
