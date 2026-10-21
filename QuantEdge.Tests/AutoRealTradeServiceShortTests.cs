using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
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

    private AutoRealTradeService CreateService(IRealTradeLockService? tradeLock = null, ICacheService? cache = null) => new(
        _repo.Object, _broker.Object, Mock.Of<IZerodhaSessionRepository>(), Mock.Of<IIndianHolidayRepository>(),
        _marketHours.Object, cache ?? Mock.Of<ICacheService>(), NullLogger<AutoRealTradeService>.Instance,
        tradeLock: tradeLock);

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
    public async Task StaleShortFromAnEarlierDay_IsClosedInQuantEdgeWithoutAnOrder()
    {
        var pos = OpenShort(entry: 100m, qty: 10, openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));
        _repo.Setup(r => r.GetLastStoredCloseAsync("SBIN", It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(98m);

        Assert.True(await CreateService().EvaluateAndExecuteRealSellAsync(pos, 104m, User));

        Assert.Empty(_placed); // never a BUY at Zerodha - it would open a new long
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
        Assert.Equal(20m, pos.RealizedPnl); // (100 - 98) x 10, at the stored close
        var cover = Assert.Single(_orders);
        Assert.Equal(TradeSide.BUY, cover.Side);
        Assert.True(cover.IsShort);
        Assert.Null(cover.BrokerOrderId);
        var exit = Assert.Single(_history);
        Assert.True(exit.IsExit);
        Assert.Equal(AutoRealTradeService.StaleShortExitReason, exit.ExitReason);
        Assert.Contains(_logs, l => l.ActionType == "SHORT_STALE_CLOSED");
    }

    [Fact]
    public async Task StaleShort_WithNoStoredPrice_ClosesAtEntry()
    {
        var pos = OpenShort(entry: 100m, qty: 10, openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));

        Assert.True(await CreateService().SquareOffSinglePositionAsync(pos.Id, userId: User));

        Assert.Empty(_placed);
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
        Assert.Equal(0m, pos.RealizedPnl);
    }

    [Fact]
    public async Task ManualExit_OnAStaleShort_PlacesNoOrder()
    {
        var pos = OpenShort(entry: 100m, qty: 10, openedUtc: IstToUtc(TradingDayIst.AddDays(-2)));

        var (success, _) = await CreateService().ManualSellAsync("SBIN", 10, 101m, "MIS", 100m, "Manual Web Square-Off", User);

        Assert.True(success);
        Assert.Empty(_placed);
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
    }

    [Fact]
    public async Task CloseStaleShorts_ClosesOnlyEarlierDayShorts()
    {
        var stale = OpenShort(entry: 100m, qty: 10, symbol: "OLD", openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));
        var today = OpenShort(entry: 100m, qty: 10);
        var oldLong = new RealPosition { Id = 1, UserId = User, Symbol = "TCS", Side = TradeSide.BUY, Quantity = 4, AverageEntryPrice = 100m, Status = PositionStatus.OPEN, OpenedAt = IstToUtc(TradingDayIst.AddDays(-3)) };
        _positions.Add(oldLong);
        _repo.Setup(r => r.GetAllOpenPositionsAsync()).ReturnsAsync(() => _positions.Where(p => p.Status == PositionStatus.OPEN).ToList());

        Assert.Equal(1, await CreateService().CloseStaleShortsAsync());

        Assert.Equal(PositionStatus.CLOSED, stale.Status);
        Assert.Equal(PositionStatus.OPEN, today.Status);
        Assert.Equal(PositionStatus.OPEN, oldLong.Status);
        Assert.Empty(_placed);
    }

    [Fact]
    public async Task SquareOffDue_WithoutATick_CoversUsingTheStoredClose()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(16));
        _repo.Setup(r => r.GetLastStoredCloseAsync("SBIN", It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(99m);
        _brokerFillPrice = 99m;

        Assert.True(await CreateService().TrySquareOffDueShortWithoutTickAsync(pos));

        Assert.Equal(("SBIN", TradeSide.BUY, 10, "MIS"), Assert.Single(_placed));
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
    }

    [Fact]
    public async Task SquareOffNotDue_WithoutATick_DoesNothing()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        _repo.Setup(r => r.GetLastStoredCloseAsync("SBIN", It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(99m);

        Assert.False(await CreateService().TrySquareOffDueShortWithoutTickAsync(pos));
        Assert.Empty(_placed);
        Assert.Equal(PositionStatus.OPEN, pos.Status);
    }

    [Fact]
    public async Task KillSwitch_SellsLongsAndBuysBackShorts()
    {
        _positions.Add(new RealPosition { Id = 1, UserId = User, Symbol = "TCS", Side = TradeSide.BUY, Quantity = 4, AverageEntryPrice = 100m, Status = PositionStatus.OPEN, OpenedAt = IstToUtc(TradingDayIst.AddDays(-3)) });
        OpenShort(entry: 100m, qty: 10);
        var old = OpenShort(entry: 100m, qty: 7, symbol: "OLD", openedUtc: IstToUtc(TradingDayIst.AddDays(-1)));

        int filled = await CreateService().SquareOffAllPositionsAsync(userId: User);

        Assert.Equal(2, filled);
        Assert.Contains(("TCS", TradeSide.SELL, 4, "CNC"), _placed);
        Assert.Contains(("SBIN", TradeSide.BUY, 10, "MIS"), _placed);
        Assert.DoesNotContain(_placed, p => p.Symbol == "OLD"); // stale short: no order...
        Assert.Equal(PositionStatus.CLOSED, old.Status);         // ...only closed in QuantEdge
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

    // ---------------- Cross-process locks (duplicate real orders) ----------------

    [Fact]
    public async Task Short_WhenAnotherProcessHoldsTheEntryLock_PlacesNoOrder()
    {
        _settings.IsAutoShortEnabled = true;
        var tradeLock = new FakeRealTradeLockService();
        tradeLock.HeldElsewhere.Add($"real_entry:{User}");

        Assert.False(await CreateService(tradeLock).EvaluateAndExecuteRealShortAsync("SBIN", 100m, 11, User, isSellSignal: true,
            engineStopLoss: 102m, engineTarget: 96m, dailyAtr: 2m));
        Assert.Empty(_placed);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task Short_DailyLimit_IsReadFromTheDatabaseNotTheCache()
    {
        _settings.IsAutoShortEnabled = true;
        _settings.MaxTradesPerDay = 3;
        // Another process already filled 3 trades today; this process's cached count is stale at 2.
        _repo.Setup(r => r.GetTodayRealTradeCountAsync(It.IsAny<int>())).ReturnsAsync(3);
        var staleCache = new Mock<ICacheService>();
        staleCache.Setup(c => c.GetAsync<int?>(It.IsAny<string>())).ReturnsAsync(2);

        Assert.False(await CreateService(new FakeRealTradeLockService(), staleCache.Object).EvaluateAndExecuteRealShortAsync(
            "SBIN", 100m, 11, User, isSellSignal: true, engineStopLoss: 102m, engineTarget: 96m, dailyAtr: 2m));
        Assert.Empty(_placed);
        Assert.Contains(_logs, l => l.Reason != null && l.Reason.Contains("Daily limit of 3"));
    }

    [Fact]
    public async Task Cover_PositionAlreadyClosedByAnotherProcess_SendsNoOrder()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        // The other instance covered it between this process reading the position and taking the exit lock.
        var stillOpenInMemory = new RealPosition
        {
            Id = pos.Id, UserId = User, Symbol = pos.Symbol, Side = TradeSide.SELL, Quantity = pos.Quantity,
            AverageEntryPrice = pos.AverageEntryPrice, StopLoss = pos.StopLoss, TakeProfit = pos.TakeProfit,
            Status = PositionStatus.OPEN, TradeType = TradeType.Auto, OpenedAt = pos.OpenedAt
        };
        pos.Status = PositionStatus.CLOSED;

        Assert.False(await CreateService(new FakeRealTradeLockService()).EvaluateAndExecuteRealSellAsync(stillOpenInMemory, 104m, User));
        Assert.Empty(_placed); // a second BUY would have opened a new long
    }

    [Fact]
    public async Task Cover_RejectedByZerodha_IsNotResentWithinTheBackoff()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        int attempts = 0;
        _broker.Setup(b => b.SquareOffLivePositionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TradeSide>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal?>()))
            .ReturnsAsync(() => { attempts++; return (false, null, 0m, "Intraday orders (MIS) are allowed only till 3:12 PM."); });
        var service = CreateService(new FakeRealTradeLockService());

        Assert.False(await service.EvaluateAndExecuteRealSellAsync(pos, 104m, User)); // stop hit -> cover rejected
        Assert.False(await service.EvaluateAndExecuteRealSellAsync(pos, 104m, User)); // next cycle: backed off

        Assert.Equal(1, attempts);
        Assert.Equal(PositionStatus.OPEN, pos.Status);
    }

    // ---------------- Same-day sync with Zerodha ----------------

    [Fact]
    public async Task Sync_ShortsFlatAtZerodha_AreClosedAtZerodhasBuyPriceWithoutAnOrder()
    {
        // 21-Oct: UNITDSPR shorted twice (3 + 3 @ 1334.70); the 15:15 cover was rejected and Zerodha squared off 6 @ 1294.40.
        var first = OpenShort(entry: 1334.70m, qty: 3, symbol: "UNITDSPR");
        var second = OpenShort(entry: 1334.70m, qty: 3, symbol: "UNITDSPR");
        SetupBrokerPositions(new ZerodhaPositionItemDto
        {
            TradingSymbol = "UNITDSPR", Product = "MIS", Quantity = 0,
            BuyQuantity = 6, BuyValue = 6 * 1294.40m, BuyPrice = 1294.40m, SellQuantity = 6, SellPrice = 1334.70m
        });
        _repo.Setup(r => r.GetFilledCoverTotalsAsync(User, "UNITDSPR", It.IsAny<DateTime>())).ReturnsAsync((0, 0m));

        Assert.Equal(2, await CreateService().SyncShortsClosedAtBrokerAsync());

        Assert.Empty(_placed);
        Assert.Equal(PositionStatus.CLOSED, first.Status);
        Assert.Equal(PositionStatus.CLOSED, second.Status);
        Assert.Equal(241.80m, first.RealizedPnl + second.RealizedPnl); // matches Zerodha's MTM
        Assert.All(_orders, o => { Assert.Null(o.BrokerOrderId); Assert.Equal(1294.40m, o.FilledPrice); Assert.True(o.IsShort); });
        Assert.All(_history, h => Assert.Equal(AutoRealTradeService.BrokerClosedShortExitReason, h.ExitReason));
        Assert.Equal(2, _logs.Count(l => l.ActionType == "SHORT_BROKER_CLOSED"));
    }

    [Fact]
    public async Task Sync_PricesOnlyThePartQuantEdgeDidNotCoverItself()
    {
        var pos = OpenShort(entry: 1334.70m, qty: 3, symbol: "UNITDSPR");
        // QuantEdge's own cover filled 3 @ 1300; Zerodha bought the other 3 @ 1294.40.
        SetupBrokerPositions(new ZerodhaPositionItemDto
        {
            TradingSymbol = "UNITDSPR", Product = "MIS", Quantity = 0,
            BuyQuantity = 6, BuyValue = 3 * 1300m + 3 * 1294.40m, BuyPrice = 1297.20m
        });
        _repo.Setup(r => r.GetFilledCoverTotalsAsync(User, "UNITDSPR", It.IsAny<DateTime>())).ReturnsAsync((3, 3 * 1300m));

        Assert.Equal(1, await CreateService().SyncShortsClosedAtBrokerAsync());

        Assert.Equal(1294.40m, Assert.Single(_orders).FilledPrice);
        Assert.Equal(120.90m, pos.RealizedPnl); // (1334.70 - 1294.40) x 3
    }

    [Fact]
    public async Task Sync_LeavesTheShortOpen_WhileQuantEdgesOwnCoverIsResting()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        _pendingOrder = new RealOrder { Symbol = "SBIN", Side = TradeSide.BUY, IsShort = true, Status = PaperOrderStatus.Open, BrokerOrderId = "X9" };
        SetupBrokerPositions(new ZerodhaPositionItemDto { TradingSymbol = "SBIN", Product = "MIS", Quantity = 0, BuyQuantity = 10, BuyValue = 990m, BuyPrice = 99m });

        Assert.Equal(0, await CreateService().SyncShortsClosedAtBrokerAsync());
        Assert.Equal(PositionStatus.OPEN, pos.Status);
    }

    [Fact]
    public async Task Sync_StillShortAtZerodhaByADifferentQuantity_OnlyAudits()
    {
        var pos = OpenShort(entry: 100m, qty: 10);
        SetupBrokerPositions(new ZerodhaPositionItemDto { TradingSymbol = "SBIN", Product = "MIS", Quantity = -4, LastPrice = 99m });

        Assert.Equal(0, await CreateService().SyncShortsClosedAtBrokerAsync());

        Assert.Equal(PositionStatus.OPEN, pos.Status);
        Assert.Empty(_orders);
        Assert.Contains(_logs, l => l.ActionType == "QUANTITY_MISMATCH");
    }

    // ---------------- Settings: short square-off before Zerodha's MIS cut-off ----------------

    [Theory]
    [InlineData("15:00", "15:15", false)] // past the 15:10 cap
    [InlineData("15:05", "15:05", false)] // cut-off must be before the square-off
    [InlineData("15:00", "15:05", true)]
    [InlineData("15:00", "15:10", true)]
    public async Task UpdateSettings_ValidatesTheShortTimes(string cutoff, string squareOff, bool valid)
    {
        _marketHours.Setup(m => m.IsWithinMarketHoursAsync(It.IsAny<DateTime?>())).ReturnsAsync(false);
        _repo.Setup(r => r.UpsertSettingsAsync(It.IsAny<RealTradeSettings>())).ReturnsAsync((RealTradeSettings s) => s);
        var dto = new RealTradeSettingsUpdateDto { ShortEntryCutoff = cutoff, ShortSquareOffTime = squareOff };

        if (valid)
            Assert.Equal(squareOff, (await CreateService().UpdateSettingsAsync(dto, User)).ShortSquareOffTime);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().UpdateSettingsAsync(dto, User));
    }

    private void SetupBrokerPositions(params ZerodhaPositionItemDto[] rows)
    {
        _repo.Setup(r => r.GetAllOpenPositionsAsync()).ReturnsAsync(() => _positions.Where(p => p.Status == PositionStatus.OPEN).ToList());
        _broker.Setup(b => b.GetLivePositionsAsync(It.IsAny<int>()))
            .ReturnsAsync((true, new ZerodhaPositionsDto { Net = rows.ToList() }, "ok"));
    }

    // In-memory IRealTradeLockService: a key in HeldElsewhere behaves as if another process holds it.
    private sealed class FakeRealTradeLockService : IRealTradeLockService
    {
        public HashSet<string> HeldElsewhere { get; } = new();
        private readonly HashSet<string> _held = new();

        public Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            if (HeldElsewhere.Contains(key) || !_held.Add(key)) return Task.FromResult<IAsyncDisposable?>(null);
            return Task.FromResult<IAsyncDisposable?>(new Release(() => _held.Remove(key)));
        }

        private sealed class Release(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
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
