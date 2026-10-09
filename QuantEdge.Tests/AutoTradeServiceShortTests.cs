using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;

namespace QuantEdge.Tests;

/// <summary>Auto Paper Short Selling - paper repositories are mocked; this service has no broker dependency at all.</summary>
public class AutoTradeServiceShortTests : ClockedTest
{
    private const string User = "default_user";

    private readonly Mock<IAutoTradeRepository> _repo = new();
    private readonly Mock<IPaperTradingRepository> _paperRepo = new();
    private readonly Mock<IPaperTradingService> _paperService = new();
    private readonly Mock<IMarketHoursService> _marketHours = new();
    private readonly List<PaperOrder> _orders = new();
    private readonly List<PaperPosition> _positions = new();
    private readonly List<PaperTradeHistory> _history = new();
    private readonly List<AutoTradeExecutionLog> _logs = new();
    private AutoTradeSettings _settings = new()
    {
        UserId = User,
        IsAutoTradeEnabled = true,
        AvailableCapital = 100000m,
        FixedAmountPerTrade = 20000m,
        TradingWindowStart = "09:15",
        TradingWindowEnd = "15:30",
        EntryDelayMinutes = 15,
        MinConditionsMatch = 10
    };

    public AutoTradeServiceShortTests()
    {
        _repo.Setup(r => r.GetSettingsAsync(It.IsAny<string>())).ReturnsAsync(() => _settings);
        _repo.Setup(r => r.GetTodayAutoTradeCountAsync(It.IsAny<string>())).ReturnsAsync(0);
        _repo.Setup(r => r.LogExecutionAsync(It.IsAny<AutoTradeExecutionLog>())).Callback<AutoTradeExecutionLog>(_logs.Add).Returns(Task.CompletedTask);
        _marketHours.Setup(m => m.IsWithinMarketHoursAsync(It.IsAny<DateTime?>())).ReturnsAsync(true);
        _paperService.Setup(s => s.GetOpenPositionsAsync(It.IsAny<string>())).ReturnsAsync(() => _positions.Where(p => p.Status == PositionStatus.OPEN).ToList());

        _paperRepo.Setup(r => r.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(new PaperAccount { Id = 1, CurrentBalance = 100000m });
        _paperRepo.Setup(r => r.GetTradeHistoryPagedAsync(It.IsAny<int>(), It.IsAny<PaperTradeHistoryFilterDto>()))
            .ReturnsAsync((Enumerable.Empty<PaperTradeHistory>(), 0));
        _paperRepo.Setup(r => r.GetOpenPositionBySymbolAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync((int _, string s) => _positions.FirstOrDefault(p => p.Symbol == s && p.Status == PositionStatus.OPEN));
        _paperRepo.Setup(r => r.CreateOrderAsync(It.IsAny<PaperOrder>()))
            .ReturnsAsync((PaperOrder o) => { o.Id = _orders.Count + 1; _orders.Add(o); return o; });
        _paperRepo.Setup(r => r.UpsertPositionAsync(It.IsAny<PaperPosition>()))
            .ReturnsAsync((PaperPosition p) => { p.Id = _positions.Count + 1; _positions.Add(p); return p; });
        _paperRepo.Setup(r => r.RecordTradeHistoryAsync(It.IsAny<PaperTradeHistory>())).Callback<PaperTradeHistory>(_history.Add).Returns(Task.CompletedTask);
        _paperRepo.Setup(r => r.ClosePositionAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>()))
            .ReturnsAsync((int id, decimal _, decimal pnl, string? _) =>
            {
                var p = _positions.First(x => x.Id == id);
                p.Status = PositionStatus.CLOSED;
                p.RealizedPnl = pnl;
                return true;
            });
    }

    private AutoTradeService CreateService() => new(
        _repo.Object, _paperRepo.Object, _paperService.Object, Mock.Of<IIndianHolidayRepository>(), _marketHours.Object,
        Mock.Of<ICacheService>(), NullLogger<AutoTradeService>.Instance);

    private Task<bool> Short(string symbol = "INFY", decimal price = 100m) =>
        CreateService().EvaluateAndExecuteAutoShortAsync(symbol, price, 11, User, isSellSignal: true,
            engineStopLoss: 102m, engineTarget: 96m, dailyAtr: 2m);

    [Fact]
    public async Task Short_IsOffByDefault()
    {
        Assert.False(new AutoTradeSettings().IsAutoShortEnabled);

        bool executed = await Short();

        Assert.False(executed);
        Assert.Empty(_orders);
        Assert.Empty(_positions);
    }

    [Fact]
    public async Task Short_WhenEnabled_OpensASimulatedShortWithMirroredLevels()
    {
        _settings.IsAutoShortEnabled = true;

        bool executed = await Short();

        Assert.True(executed);
        var order = Assert.Single(_orders);
        Assert.Equal(TradeSide.SELL, order.Side);
        Assert.Equal(TradeType.Auto, order.TradeType);
        var pos = Assert.Single(_positions);
        Assert.Equal(TradeSide.SELL, pos.Side);
        Assert.Equal(103m, pos.StopLoss);   // 100 + 1.5 x ATR 2
        Assert.Equal(94m, pos.TakeProfit);  // 100 - 3 x ATR 2
        Assert.True(pos.Quantity > 0);
        var entryRow = Assert.Single(_history);
        Assert.Equal(TradeSide.SELL, entryRow.Side);
        Assert.False(entryRow.IsExit);
        Assert.Contains(_logs, l => l.ActionType == "AUTO_SHORT");
    }

    [Fact]
    public async Task Short_NeedsTheMasterSwitchToo()
    {
        _settings.IsAutoShortEnabled = true;
        _settings.IsAutoTradeEnabled = false;

        Assert.False(await Short());
        Assert.Empty(_orders);
    }

    [Fact]
    public async Task Short_IsRejectedAfterTheEntryCutoff()
    {
        _settings.IsAutoShortEnabled = true;
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(5)); // 15:05, cut-off 15:00

        Assert.False(await Short());
        Assert.Empty(_orders);
        Assert.Contains(_logs, l => l.Reason!.StartsWith("Short selling closed"));
    }

    [Fact]
    public async Task Short_IsRejectedWhenTheSymbolAlreadyHasAnOpenPosition()
    {
        _settings.IsAutoShortEnabled = true;
        _positions.Add(new PaperPosition { Id = 1, Symbol = "INFY", Side = TradeSide.BUY, Status = PositionStatus.OPEN, TradeType = TradeType.Auto, Quantity = 10, AverageEntryPrice = 100m });

        Assert.False(await Short());
        Assert.Empty(_orders);
    }

    [Fact]
    public async Task Short_RespectsTheDailyTradeLimit()
    {
        _settings.IsAutoShortEnabled = true;
        _repo.Setup(r => r.GetTodayAutoTradeCountAsync(It.IsAny<string>())).ReturnsAsync(_settings.MaxTradesPerDay);

        Assert.False(await Short());
        Assert.Empty(_orders);
    }

    [Fact]
    public async Task Short_RejectsWhenPaperMarginIsTooLow()
    {
        _settings.IsAutoShortEnabled = true;
        _paperRepo.Setup(r => r.GetAccountAsync(It.IsAny<string>())).ReturnsAsync(new PaperAccount { Id = 1, CurrentBalance = 1000m });

        Assert.False(await Short());
        Assert.Empty(_orders);
    }

    [Fact]
    public async Task Cover_AtTarget_BooksShortProfit()
    {
        var pos = OpenShort(entry: 100m, sl: 103m, tp: 94m, qty: 10);

        bool closed = await CreateService().EvaluateAndExecuteAutoSellAsync(pos, 93.5m, User);

        Assert.True(closed);
        Assert.Equal(PositionStatus.CLOSED, pos.Status);
        Assert.Equal(65m, pos.RealizedPnl); // (100 - 93.5) x 10
        var exit = Assert.Single(_history);
        Assert.Equal(TradeSide.BUY, exit.Side);
        Assert.True(exit.IsExit);
        Assert.Equal("Target Hit", exit.ExitReason);
    }

    [Fact]
    public async Task Cover_AtStop_BooksShortLoss()
    {
        var pos = OpenShort(entry: 100m, sl: 103m, tp: 94m, qty: 10);

        Assert.True(await CreateService().EvaluateAndExecuteAutoSellAsync(pos, 103.5m, User));
        Assert.Equal(-35m, pos.RealizedPnl);
    }

    [Fact]
    public async Task Cover_SquareOff_RunsEvenOutsideTheUsersTradingWindow()
    {
        _settings.TradingWindowEnd = "15:00";
        var pos = OpenShort(entry: 100m, sl: 103m, tp: 94m, qty: 10);
        SetIstNow(TradingDayIst.Date.AddHours(15).AddMinutes(16));

        Assert.True(await CreateService().EvaluateAndExecuteAutoSellAsync(pos, 101m, User));
        Assert.Equal(SwingTradeRules.ShortSquareOffReason, _history.Single().ExitReason);
        Assert.Contains(_logs, l => l.ActionType == "AUTO_SQUARE_OFF");
    }

    [Fact]
    public async Task Cover_HoldsBetweenStopAndTarget()
    {
        var pos = OpenShort(entry: 100m, sl: 103m, tp: 94m, qty: 10);

        Assert.False(await CreateService().EvaluateAndExecuteAutoSellAsync(pos, 101m, User));
        Assert.Equal(PositionStatus.OPEN, pos.Status);
    }

    [Fact]
    public async Task LongBuy_IsUnchanged()
    {
        bool executed = await CreateService().EvaluateAndExecuteAutoBuyAsync("TCS", 100m, 11, User, isBuySignal: true, dailyAtr: 2m);

        Assert.True(executed);
        Assert.Equal(TradeSide.BUY, Assert.Single(_orders).Side);
        var pos = Assert.Single(_positions);
        Assert.Equal(TradeSide.BUY, pos.Side);
        Assert.Equal(97m, pos.StopLoss);
        Assert.Equal(106m, pos.TakeProfit);
        Assert.Contains(_logs, l => l.ActionType == "AUTO_BUY");
    }

    [Fact]
    public async Task LongSell_IsUnchanged()
    {
        var pos = new PaperPosition { Id = 50, Symbol = "TCS", Side = TradeSide.BUY, Quantity = 10, AverageEntryPrice = 100m, StopLoss = 97m, TakeProfit = 106m, Status = PositionStatus.OPEN, TradeType = TradeType.Auto, OpenedAt = IstToUtc(TradingDayIst.AddDays(-1)) };
        _positions.Add(pos);

        Assert.True(await CreateService().EvaluateAndExecuteAutoSellAsync(pos, 107m, User));
        Assert.Equal(70m, pos.RealizedPnl);
        var exit = Assert.Single(_history);
        Assert.Equal(TradeSide.SELL, exit.Side);
        Assert.True(exit.IsExit);
    }

    private PaperPosition OpenShort(decimal entry, decimal sl, decimal tp, int qty)
    {
        var pos = new PaperPosition
        {
            Id = 100 + _positions.Count, AccountId = 1, Symbol = "INFY", Side = TradeSide.SELL, Quantity = qty,
            AverageEntryPrice = entry, StopLoss = sl, TakeProfit = tp, Status = PositionStatus.OPEN, TradeType = TradeType.Auto,
            OpenedAt = IstToUtc(TradingDayIst.AddHours(-1))
        };
        _positions.Add(pos);
        return pos;
    }
}
