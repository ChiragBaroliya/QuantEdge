using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>Manual Paper Limit Orders - placement, fill and expiry. The manual_paper_* repository is mocked in memory.</summary>
public class ManualPaperLimitOrderTests : ClockedTest
{
    private const int User = 1;

    private readonly Mock<IManualPaperTradeRepository> _repo = new();
    private readonly Mock<IMarketHoursService> _marketHours = new();
    private readonly List<ManualPaperPendingOrderDto> _pending = new();
    private readonly List<ManualPaperPosition> _positions = new();
    private readonly List<ManualPaperTradeExecutionLog> _logs = new();
    private readonly Dictionary<int, string> _cancelled = new();
    private readonly Dictionary<string, decimal> _prices = new() { ["INFY"] = 1500m };
    private ManualPaperDashboardDto _dashboard = new();
    private readonly ManualPaperTradeSettings _settings = new()
    {
        UserId = User,
        IsManualTradeEnabled = true,
        AvailableCapital = 100000m,
        StopLossPct = 3m,
        TrailingSlPct = 2m,
        ProfitTargetPct = 5m,
        TradingWindowStart = "09:15",
        TradingWindowEnd = "15:30",
        EntryDelayMinutes = 15,
        ShortEntryCutoff = "15:00",
        ShortSquareOffTime = "15:15"
    };

    public ManualPaperLimitOrderTests()
    {
        _marketHours.Setup(m => m.IsWithinMarketHoursAsync(It.IsAny<DateTime?>())).ReturnsAsync(true);
        _repo.Setup(r => r.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(() => _settings);
        _repo.Setup(r => r.GetTodayManualTradeCountAsync(It.IsAny<int>())).ReturnsAsync(() => _pending.Count);
        _repo.Setup(r => r.LogExecutionAsync(It.IsAny<ManualPaperTradeExecutionLog>())).Callback<ManualPaperTradeExecutionLog>(_logs.Add).Returns(Task.CompletedTask);
        _repo.Setup(r => r.GetDashboardAsync(It.IsAny<int>(), It.IsAny<DateTime>())).ReturnsAsync(() => _dashboard);
        _repo.Setup(r => r.GetOpenPositionsAsync(It.IsAny<int>())).ReturnsAsync(() => _positions.ToList());
        _repo.Setup(r => r.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> symbols) => symbols
                .Where(_prices.ContainsKey)
                .Select(s => new ManualPaperPriceDto { Symbol = s, Ltp = _prices[s], PriceTime = DateTime.UtcNow })
                .ToList());
        _repo.Setup(r => r.GetPendingOrdersAsync(It.IsAny<int?>())).ReturnsAsync(() => _pending.ToList());

        _repo.Setup(r => r.CreateEntryAsync(It.IsAny<int>(), It.IsAny<TradeSide>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((int _, TradeSide side, string symbol, int qty, decimal price, decimal sl, decimal _, decimal tp, string _, string _, bool _) =>
                AddPosition(side, symbol, qty, price, sl, tp));
        _repo.Setup(r => r.PlaceLimitOrderAsync(It.IsAny<int>(), It.IsAny<TradeSide>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((int userId, TradeSide side, string symbol, int qty, decimal limit, decimal slPct, decimal tslPct, decimal _, decimal _, string _, bool _) =>
                AddPending(side, symbol, qty, limit, slPct, tslPct, TradingDayIst).Id);
        _repo.Setup(r => r.FillLimitOrderAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>()))
            .ReturnsAsync((int orderId, decimal fill, decimal sl, decimal _, decimal tp, string _) =>
            {
                var order = _pending.FirstOrDefault(o => o.Id == orderId);
                if (order == null) return (int?)null;
                _pending.Remove(order);
                return AddPosition(order.Side, order.Symbol, order.Quantity, fill, sl, tp);
            });
        _repo.Setup(r => r.CancelOrderAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((int _, int orderId, string reason) =>
            {
                var order = _pending.FirstOrDefault(o => o.Id == orderId);
                if (order == null) return false;
                _pending.Remove(order);
                _cancelled[orderId] = reason;
                return true;
            });
    }

    private ManualPaperTradeService CreateService() =>
        new(_repo.Object, _marketHours.Object, NullLogger<ManualPaperTradeService>.Instance);

    private int AddPosition(TradeSide side, string symbol, int qty, decimal price, decimal sl, decimal tp)
    {
        var pos = new ManualPaperPosition
        {
            Id = _positions.Count + 1, AccountId = User, Symbol = symbol, Side = side, Quantity = qty,
            AverageEntryPrice = price, CurrentPrice = price, StopLoss = sl, TakeProfit = tp, Status = PositionStatus.OPEN
        };
        _positions.Add(pos);
        return pos.Id;
    }

    private ManualPaperPendingOrderDto AddPending(TradeSide side, string symbol, int qty, decimal limit, decimal? slPct, decimal? tslPct,
        DateTime createdIst, DateTime? triggerIst = null, decimal? triggerOpen = null)
    {
        var order = new ManualPaperPendingOrderDto
        {
            Id = 100 + _pending.Count + _cancelled.Count, UserId = User, Symbol = symbol, Side = side, Quantity = qty,
            LimitPrice = limit, StopLossPct = slPct, TrailingSlPct = tslPct, CreatedAt = IstToUtc(createdIst),
            TriggerCandleTime = triggerIst.HasValue ? IstToUtc(triggerIst.Value) : null, TriggerCandleOpen = triggerOpen
        };
        _pending.Add(order);
        return order;
    }

    // ---------------- Pure rules ----------------

    [Theory]
    [InlineData(1480.03, 1480.05)]
    [InlineData(1480.02, 1480.00)]
    [InlineData(99.975, 100.00)]
    public void RoundToTick_RoundsToFivePaise(decimal price, decimal expected) =>
        Assert.Equal(expected, ManualPaperTradeService.RoundToTick(price));

    [Theory]
    [InlineData(TradeSide.BUY, 101, true)]
    [InlineData(TradeSide.BUY, 100, true)]
    [InlineData(TradeSide.BUY, 99, false)]
    [InlineData(TradeSide.SELL, 99, true)]
    [InlineData(TradeSide.SELL, 101, false)]
    public void IsMarketableLimit_BuyAtOrAboveAndShortAtOrBelowThePrice(TradeSide side, decimal limit, bool expected) =>
        Assert.Equal(expected, ManualPaperTradeService.IsMarketableLimit(side, limit, 100m));

    [Fact]
    public void CheckLimitPrice_RejectsZeroAndPricesTooFarAway()
    {
        Assert.NotNull(ManualPaperTradeService.CheckLimitPrice(0m, 100m));
        Assert.NotNull(ManualPaperTradeService.CheckLimitPrice(110.5m, 100m));
        Assert.NotNull(ManualPaperTradeService.CheckLimitPrice(10m, 100m));      // ₹10 typed for ₹100
        Assert.Null(ManualPaperTradeService.CheckLimitPrice(90m, 100m));          // exactly 10% away is allowed
        Assert.Null(ManualPaperTradeService.CheckLimitPrice(98.5m, 100m));
    }

    [Theory]
    [InlineData(TradeSide.BUY, 100, 98, 98)]      // gapped down through the limit - fills at the better open
    [InlineData(TradeSide.BUY, 100, 101, 100)]    // opened above, traded down to the limit
    [InlineData(TradeSide.SELL, 100, 103, 103)]   // gapped up through a short limit - better price
    [InlineData(TradeSide.SELL, 100, 99, 100)]
    public void LimitFillPrice_IsTheLimitOrBetter(TradeSide side, decimal limit, decimal open, decimal expected) =>
        Assert.Equal(expected, ManualPaperTradeService.LimitFillPrice(side, limit, open));

    [Fact]
    public void LimitOrderExpiry_IsWindowEnd_OrShortCutoffForShorts()
    {
        Assert.Equal(new TimeSpan(15, 30, 0), ManualPaperTradeService.LimitOrderExpiry(TradeSide.BUY, _settings));
        Assert.Equal(new TimeSpan(15, 0, 0), ManualPaperTradeService.LimitOrderExpiry(TradeSide.SELL, _settings));
    }

    // ---------------- Placement ----------------

    [Fact]
    public async Task LimitBuy_BelowPrice_RestsAsPending_WithoutOpeningAPosition()
    {
        var (success, message) = await CreateService().PlaceManualLimitOrderAsync(TradeSide.BUY, "INFY", 1480.02m, 10, 3m, 2m);

        Assert.True(success, message);
        Assert.Contains("Pending", message);
        Assert.Empty(_positions);
        var order = Assert.Single(_pending);
        Assert.Equal(1480.00m, order.LimitPrice);            // rounded to the tick
        Assert.Equal(3m, order.StopLossPct);
        Assert.Contains(_logs, l => l.ActionType == "LIMIT_PLACED");
    }

    [Fact]
    public async Task LimitBuy_AtOrAbovePrice_FillsNowAtTheStoredPrice()
    {
        var (success, message) = await CreateService().PlaceManualLimitOrderAsync(TradeSide.BUY, "INFY", 1510m, 10, 3m, 2m);

        Assert.True(success, message);
        Assert.Empty(_pending);
        var pos = Assert.Single(_positions);
        Assert.Equal(1500m, pos.AverageEntryPrice);           // the stored price, better than the 1510 limit
    }

    [Fact]
    public async Task LimitShort_AbovePrice_RestsAsPending()
    {
        var (success, message) = await CreateService().PlaceManualLimitOrderAsync(TradeSide.SELL, "INFY", 1530m, 10, 3m, 2m);

        Assert.True(success, message);
        Assert.Equal(TradeSide.SELL, Assert.Single(_pending).Side);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task LimitPrice_TooFarFromThePrice_IsRejected()
    {
        var (success, _) = await CreateService().PlaceManualLimitOrderAsync(TradeSide.BUY, "INFY", 150m, 10, 3m, 2m);

        Assert.False(success);
        Assert.Empty(_pending);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task PendingOrder_BlocksAnotherEntryInTheSameSymbol()
    {
        AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst);

        var market = await CreateService().ExecuteManualBuyAsync("INFY", 1500m, 10, 3m, 2m);
        var limit = await CreateService().PlaceManualLimitOrderAsync(TradeSide.BUY, "INFY", 1470m, 10, 3m, 2m);

        Assert.False(market.Success);
        Assert.Contains("Pending limit order", market.Message);
        Assert.False(limit.Success);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task HeldCapital_OfPendingOrders_IsNotAvailableForANewOrder()
    {
        _dashboard = new ManualPaperDashboardDto { ManualBlockedMargin = 90000m };

        var (success, message) = await CreateService().PlaceManualLimitOrderAsync(TradeSide.BUY, "INFY", 1480m, 10, 3m, 2m);

        Assert.False(success);
        Assert.Contains("Insufficient Manual Capital", message);
    }

    // ---------------- Worker pass: fill / expire ----------------

    [Fact]
    public async Task TriggeredBuy_FillsAtTheLimit_WithLevelsFromTheFillPrice()
    {
        var order = AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst.AddMinutes(-30),
            triggerIst: TradingDayIst.AddMinutes(-5), triggerOpen: 1485m);

        var (filled, cancelled) = await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Equal((1, 0), (filled, cancelled));
        var pos = Assert.Single(_positions);
        Assert.Equal(1480m, pos.AverageEntryPrice);
        Assert.True(pos.StopLoss < 1480m && pos.TakeProfit > 1480m);
        Assert.Contains(_logs, l => l.ActionType == "LIMIT_FILLED" && l.Reason!.Contains($"#{order.Id}"));
    }

    [Fact]
    public async Task TriggeredBuy_OnAGapDown_FillsAtTheBetterOpen()
    {
        AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst.AddMinutes(-30),
            triggerIst: TradingDayIst.AddMinutes(-5), triggerOpen: 1470m);

        await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Equal(1470m, Assert.Single(_positions).AverageEntryPrice);
    }

    [Fact]
    public async Task TriggeredShort_FillsAsAShortWithStopAboveEntry()
    {
        AddPending(TradeSide.SELL, "INFY", 10, 1530m, 3m, 2m, TradingDayIst.AddMinutes(-30),
            triggerIst: TradingDayIst.AddMinutes(-5), triggerOpen: 1525m);

        await CreateService().ProcessPendingLimitOrdersAsync();

        var pos = Assert.Single(_positions);
        Assert.Equal(TradeSide.SELL, pos.Side);
        Assert.Equal(1530m, pos.AverageEntryPrice);
        Assert.True(pos.StopLoss > 1530m && pos.TakeProfit < 1530m);
    }

    [Fact]
    public async Task UntriggeredOrder_StaysPending_DuringTheDay()
    {
        AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst.AddMinutes(-30));

        var result = await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Equal((0, 0), result);
        Assert.Single(_pending);
    }

    [Fact]
    public async Task UntriggeredBuy_ExpiresAtTheTradingWindowEnd()
    {
        var order = AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(30));

        var (_, cancelled) = await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Equal(1, cancelled);
        Assert.StartsWith("Expired", _cancelled[order.Id]);
        Assert.Contains(_logs, l => l.ActionType == "LIMIT_EXPIRED");
    }

    [Fact]
    public async Task UntriggeredShort_ExpiresAtTheShortCutoff_WhileABuyStaysPending()
    {
        var shortOrder = AddPending(TradeSide.SELL, "INFY", 10, 1530m, 3m, 2m, TradingDayIst);
        AddPending(TradeSide.BUY, "TCS", 5, 3900m, 3m, 2m, TradingDayIst);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(5));

        await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.True(_cancelled.ContainsKey(shortOrder.Id));
        Assert.Equal("TCS", Assert.Single(_pending).Symbol);
    }

    [Fact]
    public async Task ShortTriggeredAfterTheCutoff_ExpiresInsteadOfFilling()
    {
        var order = AddPending(TradeSide.SELL, "INFY", 10, 1530m, 3m, 2m, TradingDayIst,
            triggerIst: TradingDayIst.Date.AddHours(15).AddMinutes(2), triggerOpen: 1528m);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(3));

        await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Empty(_positions);
        Assert.StartsWith("Expired", _cancelled[order.Id]);
    }

    [Fact]
    public async Task OrderFromAnEarlierDay_Expires()
    {
        var order = AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst.AddDays(-1));

        await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.StartsWith("Expired", _cancelled[order.Id]);
    }

    [Fact]
    public async Task TriggeredOrder_IsCancelled_WhenManualTradingIsSwitchedOff()
    {
        _settings.IsManualTradeEnabled = false;
        var order = AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst.AddMinutes(-30),
            triggerIst: TradingDayIst.AddMinutes(-5), triggerOpen: 1485m);

        var (filled, cancelled) = await CreateService().ProcessPendingLimitOrdersAsync();

        Assert.Equal((0, 1), (filled, cancelled));
        Assert.Empty(_positions);
        Assert.Contains("switch is OFF", _cancelled[order.Id]);
    }

    [Fact]
    public async Task Cancel_ReleasesAPendingOrder_AndRejectsAnUnknownOne()
    {
        var order = AddPending(TradeSide.BUY, "INFY", 10, 1480m, 3m, 2m, TradingDayIst);
        var service = CreateService();

        var cancel = await service.CancelOrderAsync(order.Id);
        var again = await service.CancelOrderAsync(order.Id);

        Assert.True(cancel.Success, cancel.Message);
        Assert.Empty(_pending);
        Assert.False(again.Success);
    }
}
