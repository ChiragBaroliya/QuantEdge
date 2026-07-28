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
    private readonly IHubContext<MarketDataHub> _hubContext;
    private readonly ILogger<PaperTradingService> _logger;
    private static bool _autoTradeEnabled = false;

    public PaperTradingService(
        IPaperTradingRepository repository,
        PaperOrderValidator validator,
        PaperMatchingEngine matchingEngine,
        IHubContext<MarketDataHub> hubContext,
        ILogger<PaperTradingService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _matchingEngine = matchingEngine ?? throw new ArgumentNullException(nameof(matchingEngine));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            if (ltp <= 0m) ltp = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.AverageEntryPrice;

            decimal pnl = pos.Side == TradeSide.BUY
                ? (ltp - pos.AverageEntryPrice) * pos.Quantity
                : (pos.AverageEntryPrice - ltp) * pos.Quantity;

            pos.CurrentPrice = ltp;
            pos.UnrealizedPnl = pnl;
            totalUnrealizedPnl += pnl;
        }

        return new PaperPortfolioDto
        {
            Account = account,
            TotalUnrealizedPnl = totalUnrealizedPnl,
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

        // Deduct margin from account
        decimal newUsedMargin = account.UsedMargin + requiredMargin;
        await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, account.CurrentBalance, newUsedMargin, account.RealizedPnl);

        // If Market Order, update or open position immediately
        if (dto.OrderType == PaperOrderType.Market)
        {
            var existingPosition = await _repository.GetOpenPositionBySymbolAsync(account.Id, dto.Symbol);
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
            else if (existingPosition.Side == dto.Side)
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
            else
            {
                // Opposite direction - partial or full close
                if (dto.Quantity >= existingPosition.Quantity)
                {
                    decimal realizedPnl = existingPosition.Side == TradeSide.BUY
                        ? (executionPrice - existingPosition.AverageEntryPrice) * existingPosition.Quantity
                        : (existingPosition.AverageEntryPrice - executionPrice) * existingPosition.Quantity;

                    await _repository.ClosePositionAsync(existingPosition.Id, executionPrice, realizedPnl);
                    decimal updatedBalance = account.CurrentBalance + realizedPnl;
                    decimal updatedMargin = Math.Max(0m, newUsedMargin - (existingPosition.Quantity * existingPosition.AverageEntryPrice) - requiredMargin);
                    await _repository.UpdateAccountBalanceAndMarginAsync(account.Id, updatedBalance, updatedMargin, account.RealizedPnl + realizedPnl);
                }
            }

            // Record Trade History
            await _repository.RecordTradeHistoryAsync(new PaperTradeHistory
            {
                AccountId = account.Id,
                OrderId = createdOrder.Id,
                Symbol = dto.Symbol.ToUpper().Trim(),
                Side = dto.Side,
                Quantity = dto.Quantity,
                ExecutedPrice = executionPrice,
                RealizedPnl = 0m,
                Remarks = "Market Order Executed"
            });
        }

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

        await _repository.ClosePositionAsync(positionId, currentLtp, realizedPnl);

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
            ExecutedPrice = currentLtp,
            RealizedPnl = realizedPnl,
            Remarks = "Manual Position Closure"
        });

        await BroadcastPortfolioUpdateAsync(userId);
    }

    public async Task ResetAccountAsync(string userId = "default_user")
    {
        var account = await GetOrCreateAccountAsync(userId);
        await _repository.ResetAccountAsync(account.Id, 100000m);
        await BroadcastPortfolioUpdateAsync(userId);
    }

    public async Task ProcessTickForPaperMatchingAsync(string symbol, decimal ltp)
    {
        await _matchingEngine.ProcessTickAsync(symbol, ltp);
    }

    public async Task ProcessSignalForAutoTradeAsync(TradingSignal signal)
    {
        if (!_autoTradeEnabled || signal == null) return;
        if (signal.SignalStrength < 50m) return;

        TradeSide side = string.Equals(signal.SignalType, "BUY", StringComparison.OrdinalIgnoreCase) ? TradeSide.BUY : TradeSide.SELL;
        try
        {
            await PlaceOrderAsync(new PlacePaperOrderDto
            {
                Symbol = signal.Symbol,
                Side = side,
                OrderType = PaperOrderType.Market,
                Quantity = 25, // Default lot size
                StopLoss = side == TradeSide.BUY ? signal.EntryPrice * 0.99m : signal.EntryPrice * 1.01m,
                TakeProfit = side == TradeSide.BUY ? signal.EntryPrice * 1.02m : signal.EntryPrice * 0.98m
            }, "default_user", signal.EntryPrice);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-trade execution failed for signal on {Symbol}", signal.Symbol);
        }
    }

    public Task SetAutoTradeStatusAsync(bool enabled)
    {
        _autoTradeEnabled = enabled;
        return Task.CompletedTask;
    }

    public bool GetAutoTradeStatus() => _autoTradeEnabled;

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
            }
        }
        return positions;
    }

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

    private async Task BroadcastPortfolioUpdateAsync(string userId)
    {
        try
        {
            var portfolio = await GetPortfolioAsync(userId);
            var positions = await GetOpenPositionsAsync(userId);
            await _hubContext.Clients.All.SendAsync("ReceivePaperAccountUpdate", portfolio);
            await _hubContext.Clients.All.SendAsync("ReceivePaperPositionsUpdate", positions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast Paper Trading updates via SignalR.");
        }
    }
}
