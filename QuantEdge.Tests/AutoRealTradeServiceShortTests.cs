using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>
/// Auto Real Short Selling. The Zerodha broker is a Moq mock: every "order" is only recorded in memory here, so no real
/// order can ever reach Zerodha from these tests.
/// </summary>
public class AutoRealTradeServiceShortTests : ClockedTest
{
    private const int User = 1;

    private readonly Mock<IRealTradingRepository> _repo = new();
    private readonly Mock<IZerodhaKiteBrokerService> _broker = new(MockBehavior.Strict);
    private readonly Mock<IMarketHoursService> _marketHours = new();
    private readonly List<RealOrder> _orders = new();
    private readonly List<RealPosition> _positions = new();
    private readonly List<RealTradeHistory> _history = new();
    private readonly List<RealTradeExecutionLog> _logs = new();
    private readonly List<(string Symbol, TradeSide Side, int Qty, string Product)> _placed = new();
    private RealOrder? _pendingOrder;
    private decimal _brokerFillPrice = 100m;
    private string _brokerStatus = "COMPLETE";

    private readonly RealTradeSettings _settings = new()
    {
        UserId = User,
        IsRealTradeEnabled = true,
        AvailableCapital = 10000m,
        FixedAmountPerTrade = 1000m,
        ProductType = "CNC",
        TradingWindowStart = "09:15",
        TradingWindowEnd = "15:30",
        EntryDelayMinutes = 15,
        MinConditionsMatch = 10,
        MaxTradesPerDay = 5
    };

    public AutoRealTradeServiceShortTests()
    {
        _repo.Setup(r => r.GetSettingsAsync(It.IsAny<int>())).ReturnsAsync(() => _settings);
        _repo.Setup(r => r.GetTodayRealTradeCountAsync(It.IsAny<int>())).ReturnsAsync(0);
        _repo.Setup(r => r.GetTodayRealizedPnlAsync(It.IsAny<int>())).ReturnsAsync(0m);
        _repo.Setup(r => r.LogExecutionAsync(It.IsAny<RealTradeExecutionLog>())).Callback<RealTradeExecutionLog>(_logs.Add).Returns(Task.CompletedTask);
        _repo.Setup(r => r.GetOpenPositionsAsync(It.IsAny<int>())).ReturnsAsync(() => _positions.Where(p => p.Status == PositionStatus.OPEN).ToList());
        _repo.Setup(r => r.GetOpenPositionBySymbolAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((int _, string s) => _positions.FirstOrDefault(p => p.Symbol == s && p.Status == PositionStatus.OPEN));
        _repo.Setup(r => r.GetOpenPositionByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => _positions.FirstOrDefault(p => p.Id == id && p.Status == PositionStatus.OPEN));
        _repo.Setup(r => r.GetOpenBrokerOrderAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TradeSide>()))
            .ReturnsAsync((int _, string s, TradeSide side) => _pendingOrder != null && _pendingOrder.Symbol == s && _pendingOrder.Side == side ? _pendingOrder : null);
        _repo.Setup(r => r.CreateOrderAsync(It.IsAny<RealOrder>()))
            .ReturnsAsync((RealOrder o) => { o.Id = _orders.Count + 1; _orders.Add(o); return o; });
        _repo.Setup(r => r.UpsertPositionAsync(It.IsAny<RealPosition>()))
            .ReturnsAsync((RealPosition p) => { p.Id = 500 + _positions.Count; p.OpenedAt = SwingTradeRules.UtcNow(); _positions.Add(p); return p; });
        _repo.Setup(r => r.RecordTradeHistoryAsync(It.IsAny<RealTradeHistory>()))
            .ReturnsAsync((RealTradeHistory h) => { _history.Add(h); return h; });
        _repo.Setup(r => r.ClosePositionAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string>()))
            .Callback((int id, decimal _, decimal pnl, string _) =>
            {
                var p = _positions.First(x => x.Id == id);
                p.Status = PositionStatus.CLOSED;
                p.RealizedPnl = pnl;
            })
            .Returns(Task.CompletedTask);
        _marketHours.Setup(m => m.IsWithinMarketHoursAsync(It.IsAny<DateTime?>())).ReturnsAsync(true);

        _broker.Setup(b => b.ValidateSessionTokenAsync(It.IsAny<int>())).ReturnsAsync((true, "token", "key", "ok"));
        _broker.Setup(b => b.GetEquityMarginsAsync(It.IsAny<int>())).ReturnsAsync((true, 50000m, 0m, "ok"));
        _broker.Setup(b => b.GetLtpQuotesAsync(It.IsAny<IEnumerable<(string, string)>>(), It.IsAny<int>()))
            .ReturnsAsync((IEnumerable<(string Symbol, string Exchange)> list, int _) =>
                (true, list.ToDictionary(x => x.Symbol, _ => 100m), "ok"));
        _broker.Setup(b => b.PlaceLiveOrderAsync(It.IsAny<string>(), It.IsAny<TradeSide>(), It.IsAny<int>(), It.IsAny<PaperOrderType>(),
                It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal?>()))
            .ReturnsAsync((string s, TradeSide side, int q, PaperOrderType _, decimal _, string product, int _, decimal? _) =>
            {
                _placed.Add((s, side, q, product));
                return (true, $"ORD{_placed.Count}", 0m, "placed");
            });
        _broker.Setup(b => b.SquareOffLivePositionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TradeSide>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal?>()))
            .ReturnsAsync((string s, int q, TradeSide positionSide, decimal _, string product, int _, decimal? _) =>
            {
                // Same rule as ZerodhaKiteBrokerService: the exit is the opposite side of the position.
                _placed.Add((s, positionSide == TradeSide.BUY ? TradeSide.SELL : TradeSide.BUY, q, product));
                return (true, $"ORD{_placed.Count}", 0m, "placed");
            });
        _broker.Setup(b => b.GetOrderStatusAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(() => (true, _brokerStatus, _brokerFillPrice, 0, "status"));
    }

    private AutoRealTradeService CreateService() => new(
        _repo.Object, _broker.Object, Mock.Of<IZerodhaSessionRepository>(), Mock.Of<IIndianHolidayRepository>(),
        _marketHours.Object, Mock.Of<ICacheService>(), NullLogger<AutoRealTradeService>.Instance);

    private Task<bool> Short(string symbol = "SBIN") =>
        CreateService().EvaluateAndExecuteRealShortAsync(symbol, 100m, 11, User, isSellSignal: true,
            engineStopLoss: 102m, engineTarget: 96m, dailyAtr: 2m);

    // ---------------- Entry ----------------

    [Fact]
    public async Task Short_IsOffByDefault_AndPlacesNoOrder()
    {
        Assert.False(new RealTradeSettings().IsAutoShortEnabled);

        Assert.False(await Short());
        Assert.Empty(_placed);
        Assert.Empty(_orders);
    }

    [Fact]
    public async Task Short_NeedsTheRealMasterSwitch()
    {
        _settings.IsAutoShortEnabled = true;
        _settings.IsRealTradeEnabled = false;

        Assert.False(await Short());
        Assert.Empty(_placed);
    }

    [Fact]
    public async Task Short_WhenEnabled_PlacesAnMisSellAndOpensAShortPosition()
    {
        _settings.IsAutoShortEnabled = true;

        Assert.True(await Short());

        var placed = Assert.Single(_placed);
        Assert.Equal(("SBIN", TradeSide.SELL, 10, "MIS"), placed); // 1000 / 100 = 10 shares, always MIS
        var order = Assert.Single(_orders);
        Assert.True(order.IsShort);
        Assert.Equal(TradeSide.SELL, order.Side);
        Assert.Equal(PaperOrderStatus.Filled, order.Status);
        var pos = Assert.Single(_positions);
        Assert.Equal(TradeSide.SELL, pos.Side);
        Assert.Equal(103m, pos.StopLoss);
        Assert.Equal(94m, pos.TakeProfit);
        Assert.False(Assert.Single(_history).IsExit);
        Assert.Contains(_logs, l => l.ActionType == "REAL_SHORT");
    }

    [Fact]
    public async Task Short_RestingOrder_IsRecordedOpenWithoutAPosition()
    {
        _settings.IsAutoShortEnabled = true;
        _brokerStatus = "OPEN";

        Assert.True(await Short());

        var order = Assert.Single(_orders);
        Assert.Equal(PaperOrderStatus.Open, order.Status);
        Assert.True(order.IsShort);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task Short_BrokerRejection_OpensNothing()
    {
        _settings.IsAutoShortEnabled = true;
        _brokerStatus = "REJECTED";

        Assert.False(await Short());
        Assert.Equal(PaperOrderStatus.Rejected, Assert.Single(_orders).Status);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task Short_IsBlockedByAnOpenPositionOrPendingOrder()
    {
        _settings.IsAutoShortEnabled = true;
        _positions.Add(new RealPosition { Id = 1, Symbol = "SBIN", Side = TradeSide.BUY, Quantity = 5, AverageEntryPrice = 100m, Status = PositionStatus.OPEN });
        Assert.False(await Short());

        _positions.Clear();
        _pendingOrder = new RealOrder { Symbol = "SBIN", Side = TradeSide.BUY, Status = PaperOrderStatus.Open, BrokerOrderId = "X1" };
        Assert.False(await Short());

        Assert.Empty(_placed);
    }

    [Fact]
    public async Task Short_RejectsWhenBrokerMarginIsTooLow()
    {
        _settings.IsAutoShortEnabled = true;
        _broker.Setup(b => b.GetEquityMarginsAsync(It.IsAny<int>())).ReturnsAsync((true, 500m, 0m, "ok"));

        Assert.False(await Short());
        Assert.Empty(_placed);
        Assert.Contains(_logs, l => l.Reason!.StartsWith("Insufficient Broker Capital"));
    }

    [Fact]
    public async Task Short_IsRejectedAfterTheEntryCutoff()
    {
        _settings.IsAutoShortEnabled = true;
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(1));

        Assert.False(await Short());
        Assert.Empty(_placed);
    }

    [Fact]
    public async Task LongBuy_IsBlockedByAPendingShortEntry()
    {
        _pendingOrder = new RealOrder { Symbol = "SBIN", Side = TradeSide.SELL, IsShort = true, Status = PaperOrderStatus.Open, BrokerOrderId = "X2" };

        Assert.False(await CreateService().EvaluateAndExecuteRealBuyAsync("SBIN", 100m, 11, User, isBuySignal: true, dailyAtr: 2m));
        Assert.Empty(_placed);
    }

    [Fact]
    public async Task LongBuy_IsUnchanged_UsesConfiguredProduct()
    {
        Assert.True(await CreateService().EvaluateAndExecuteRealBuyAsync("TCS", 100m, 11, User, isBuySignal: true, dailyAtr: 2m));

        Assert.Equal(("TCS", TradeSide.BUY, 10, "CNC"), Assert.Single(_placed));
        Assert.False(Assert.Single(_orders).IsShort);
        Assert.Equal(TradeSide.BUY, Assert.Single(_positions).Side);
    }

    // ---------------- Exit ----------------

    [Fact]
    public async Task Cover_StopHit_BuysBackWithMisAndBooksTheLoss()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        _brokerFillPrice = 103.5m;

        Assert.True(await CreateService().EvaluateAndExecuteRealSellAsync(pos, 103.4m, User));

        Assert.Equal(("SBIN", TradeSide.BUY, 10, "MIS"), Assert.Single(_placed));
        var order = Assert.Single(_orders);
        Assert.Equal(TradeSide.BUY, order.Side);
        Assert.True(order.IsShort);
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
        Assert.Equal(-35m, pos.RealizedPnl); // (100 - 103.5) x 10
        var exit = Assert.Single(_history);
        Assert.Equal(TradeSide.BUY, exit.Side);
        Assert.True(exit.IsExit);
        Assert.Contains(_logs, l => l.ActionType == "REAL_COVER");
    }

    [Fact]
    public async Task Cover_SquareOffAtTime_EvenOutsideTheTradingWindow()
    {
        _settings.TradingWindowEnd = "15:00";
        var pos = OpenShort(entry: 100m, qty: 10);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(15));
        _brokerFillPrice = 99m;

        Assert.True(await CreateService().EvaluateAndExecuteRealSellAsync(pos, 99m, User));
        Assert.Equal(TradeSide.BUY, Assert.Single(_placed).Side);
        Assert.Equal(10m, pos.RealizedPnl);
    }

    [Fact]
    public async Task Cover_SkipsWhenACoverOrderIsAlreadyPending()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        _pendingOrder = new RealOrder { Symbol = "SBIN", Side = TradeSide.BUY, IsShort = true, Status = PaperOrderStatus.Open, BrokerOrderId = "X3" };

        Assert.False(await CreateService().EvaluateAndExecuteRealSellAsync(pos, 104m, User));
        Assert.Empty(_placed);
    }

    [Fact]
    public async Task StaleShortFromAnEarlierDay_IsFlaggedNotBoughtBack()
    {
        var pos = OpenShort(entry: 100m, qty: 10, openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));

        Assert.False(await CreateService().EvaluateAndExecuteRealSellAsync(pos, 104m, User));
        Assert.Empty(_placed);
        Assert.Equal(PositionStatus.OPEN, pos.Status);
        Assert.Contains(_logs, l => l.ActionType == "SHORT_STALE");
    }

    [Fact]
    public async Task KillSwitch_SellsLongsAndBuysBackShorts()
    {
        _positions.Add(new RealPosition { Id = 1, UserId = User, Symbol = "TCS", Side = TradeSide.BUY, Quantity = 4, AverageEntryPrice = 100m, Status = PositionStatus.OPEN, OpenedAt = IstToUtc(TradingDayIst.AddDays(-3)) });
        OpenShort(entry: 100m, qty: 10);
        OpenShort(entry: 100m, qty: 7, symbol: "OLD", openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));

        int filled = await CreateService().SquareOffAllPositionsAsync(userId: User);

        Assert.Equal(2, filled);
        Assert.Contains(("TCS", TradeSide.SELL, 4, "CNC"), _placed);
        Assert.Contains(("SBIN", TradeSide.BUY, 10, "MIS"), _placed);
        Assert.DoesNotContain(_placed, p => p.Symbol == "OLD"); // stale short left for a manual check
    }

    // ---------------- Reconcile (pending orders confirmed later) ----------------

    [Fact]
    public async Task Reconcile_PendingShortEntryFill_OpensAShort()
    {
        var pending = new RealOrder { Id = 77, UserId = User, BrokerOrderId = "B77", Symbol = "SBIN", Side = TradeSide.SELL, IsShort = true, Quantity = 10, Price = 100m, StopLoss = 103m, TakeProfit = 94m, Status = PaperOrderStatus.Open, TradeType = TradeType.Auto };
        _repo.Setup(r => r.GetAllPendingBrokerOrdersAsync()).ReturnsAsync(new[] { pending });
        _brokerFillPrice = 99m;

        await CreateService().ReconcilePendingRealOrdersAsync();

        var pos = Assert.Single(_positions);
        Assert.Equal(TradeSide.SELL, pos.Side);
        Assert.Equal(99m, pos.AverageEntryPrice);
        Assert.Equal(102m, pos.StopLoss);   // shifted with the fill (-1)
        Assert.Equal(93m, pos.TakeProfit);
    }

    [Fact]
    public async Task Reconcile_PendingCoverFill_ClosesTheShort()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        var pending = new RealOrder { Id = 78, UserId = User, BrokerOrderId = "B78", Symbol = "SBIN", Side = TradeSide.BUY, IsShort = true, Quantity = 10, Price = 95m, Status = PaperOrderStatus.Open, Remarks = "Real BUY TO COVER (Target Hit)" };
        _repo.Setup(r => r.GetAllPendingBrokerOrdersAsync()).ReturnsAsync(new[] { pending });
        _brokerFillPrice = 95m;

        await CreateService().ReconcilePendingRealOrdersAsync();

        Assert.Equal(PositionStatus.CLOSED, pos.Status);
        Assert.Equal(50m, pos.RealizedPnl);
        Assert.Single(_positions); // no new long was opened by the BUY fill
    }

    [Fact]
    public async Task Reconcile_LongSellFill_NeverClosesAShort()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        var pending = new RealOrder { Id = 79, UserId = User, BrokerOrderId = "B79", Symbol = "SBIN", Side = TradeSide.SELL, IsShort = false, Quantity = 10, Price = 95m, Status = PaperOrderStatus.Open };
        _repo.Setup(r => r.GetAllPendingBrokerOrdersAsync()).ReturnsAsync(new[] { pending });

        await CreateService().ReconcilePendingRealOrdersAsync();

        Assert.Equal(PositionStatus.OPEN, pos.Status);
        Assert.Empty(_history);
    }

    private RealPosition OpenShort(decimal entry, int qty, string symbol = "SBIN", DateTime? openedUtc = null)
    {
        var pos = new RealPosition
        {
            Id = 900 + _positions.Count, UserId = User, Symbol = symbol, Side = TradeSide.SELL, Quantity = qty, AverageEntryPrice = entry,
            StopLoss = 103m, TakeProfit = 94m, Status = PositionStatus.OPEN, TradeType = TradeType.Auto,
            OpenedAt = openedUtc ?? IstToUtc(TradingDayIst.AddHours(-1))
        };
        _positions.Add(pos);
        return pos;
    }
}
