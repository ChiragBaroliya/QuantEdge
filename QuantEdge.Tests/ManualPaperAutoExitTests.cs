using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;
using QuantEdge.Infrastructure.Services;
using Xunit;
using static QuantEdge.Infrastructure.Services.ManualPaperTradeService;

namespace QuantEdge.Tests;

/// <summary>Manual Paper Auto Exit - Target / Stop Loss / Trailing SL judged from stored 1-minute candles only.</summary>
public class ManualPaperAutoExitTests : ClockedTest
{
    private static readonly DateTime FirstCandleUtc = IstToUtc(new DateTime(2026, 10, 7, 10, 1, 0));

    // Candles one minute apart starting at FirstCandleUtc: (open, high, low, close).
    private static List<ManualPaperCandleDto> Candles(params (decimal O, decimal H, decimal L, decimal C)[] bars) =>
        bars.Select((b, i) => new ManualPaperCandleDto
        {
            CandleTime = FirstCandleUtc.AddMinutes(i), CandleOpen = b.O, CandleHigh = b.H, CandleLow = b.L, CandleClose = b.C
        }).ToList();

    // Long: entry 100, SL 97, Target 105, Trailing SL 2%.
    private static AutoExitResult Long(List<ManualPaperCandleDto> candles, decimal? trail = null, decimal? best = null) =>
        EvaluateAutoExit(TradeSide.BUY, 100m, 97m, 105m, 2m, trail, best, candles);

    // Short: entry 100, SL 103, Target 95, Trailing SL 2%.
    private static AutoExitResult Short(List<ManualPaperCandleDto> candles) =>
        EvaluateAutoExit(TradeSide.SELL, 100m, 103m, 95m, 2m, null, null, candles);

    // ---------------- Long ----------------

    [Fact]
    public void Long_TargetHitInsideACandle_ExitsAtTheTarget()
    {
        var r = Long(Candles((100m, 100.8m, 99.6m, 100.5m), (100.5m, 105.5m, 100.2m, 105m)));

        Assert.Equal(AutoExitKind.Target, r.Kind);
        Assert.Equal(105m, r.ExitPrice);
        Assert.Equal(FirstCandleUtc.AddMinutes(1), r.ExitCandleTime);
    }

    [Fact]
    public void Long_StopLossHit_ExitsAtTheStop()
    {
        var r = Long(Candles((100m, 100.5m, 96.8m, 97.2m)));

        Assert.Equal(AutoExitKind.StopLoss, r.Kind);
        Assert.Equal(97m, r.ExitPrice);
    }

    [Fact]
    public void Long_CandleTouchesStopAndTarget_AssumesTheStopFirst()
    {
        var r = Long(Candles((100m, 106m, 96m, 101m)));

        Assert.Equal(AutoExitKind.StopLoss, r.Kind);
        Assert.Equal(97m, r.ExitPrice);
    }

    [Fact]
    public void Long_GapDownThroughTheStop_ExitsAtTheOpen()
    {
        var r = Long(Candles((95m, 96m, 94m, 95.5m)));

        Assert.Equal(AutoExitKind.StopLoss, r.Kind);
        Assert.Equal(95m, r.ExitPrice);
    }

    [Fact]
    public void Long_GapUpThroughTheTarget_ExitsAtTheBetterOpen()
    {
        var r = Long(Candles((106m, 107m, 105.5m, 106.5m)));

        Assert.Equal(AutoExitKind.Target, r.Kind);
        Assert.Equal(106m, r.ExitPrice);
    }

    [Fact]
    public void Long_TrailStartsOnlyOnceTrailingPctInProfit()
    {
        var notYet = Long(Candles((100m, 101.5m, 99.8m, 101m)));
        Assert.Equal(AutoExitKind.None, notYet.Kind);
        Assert.Null(notYet.TrailingStopLoss);
        Assert.Equal(101.5m, notYet.BestPrice);

        var started = Long(Candles((100m, 102.5m, 99.8m, 102m)));
        Assert.Equal(AutoExitKind.None, started.Kind);
        Assert.Equal(100.45m, started.TrailingStopLoss);             // 102.5 x (1 - 2%)
        Assert.Equal(FirstCandleUtc, started.CheckedUntil);
    }

    [Fact]
    public void Long_TrailedStopHit_ExitsAsTrailingStop()
    {
        var r = Long(Candles((100m, 102.5m, 99.8m, 102m), (102m, 102.2m, 100.3m, 100.6m)));

        Assert.Equal(AutoExitKind.TrailingStop, r.Kind);
        Assert.Equal(100.45m, r.ExitPrice);
    }

    [Fact]
    public void Long_TrailOnlyTightens()
    {
        var r = Long(Candles((100m, 104m, 101m, 103.5m), (103.5m, 103.6m, 102.5m, 103m)));

        Assert.Equal(AutoExitKind.None, r.Kind);
        Assert.Equal(101.92m, r.TrailingStopLoss);                   // set by the 104 high, not lowered by 103.6
        Assert.Equal(104m, r.BestPrice);
        Assert.Equal(FirstCandleUtc.AddMinutes(1), r.CheckedUntil);
    }

    [Fact]
    public void Long_SavedTrailState_IsContinued()
    {
        // Trail 101.92 from an earlier pass; this candle dips to 101.9.
        var r = Long(Candles((102.5m, 102.8m, 101.9m, 102m)), trail: 101.92m, best: 104m);

        Assert.Equal(AutoExitKind.TrailingStop, r.Kind);
        Assert.Equal(101.92m, r.ExitPrice);
    }

    // ---------------- Short ----------------

    [Fact]
    public void Short_TargetBelowEntry_ExitsAtTheTarget()
    {
        var r = Short(Candles((100m, 100.5m, 94.5m, 95.2m)));

        Assert.Equal(AutoExitKind.Target, r.Kind);
        Assert.Equal(95m, r.ExitPrice);
    }

    [Fact]
    public void Short_StopAboveEntry_ExitsAtTheStop()
    {
        var r = Short(Candles((100m, 103.5m, 99.5m, 103m)));

        Assert.Equal(AutoExitKind.StopLoss, r.Kind);
        Assert.Equal(103m, r.ExitPrice);
    }

    [Fact]
    public void Short_TrailFollowsTheLowAndIsHitOnTheBounce()
    {
        var r = Short(Candles((100m, 100.2m, 97.9m, 98m), (98m, 100m, 97.95m, 99.9m)));

        Assert.Equal(AutoExitKind.TrailingStop, r.Kind);
        Assert.Equal(99.86m, r.ExitPrice);                            // 97.9 x (1 + 2%)
    }

    // ---------------- Worker pass ----------------

    private readonly Mock<IManualPaperTradeRepository> _repo = new();
    private readonly List<ManualPaperTradeExecutionLog> _logs = new();

    private ManualPaperTradeService Service(ManualPaperAutoExitPositionDto position, List<ManualPaperCandleDto> candles)
    {
        _repo.Setup(r => r.GetAutoExitPositionsAsync()).ReturnsAsync(new[] { position });
        _repo.Setup(r => r.GetCandlesAsync(position.Symbol, It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync(candles);
        _repo.Setup(r => r.UpdatePositionTrailAsync(It.IsAny<int>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.ClosePositionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>()))
            .ReturnsAsync((int _, int _, decimal exit, string _) =>
                new ManualPaperCloseResult { Success = true, Symbol = position.Symbol, Quantity = position.Quantity, ExitPrice = exit });
        _repo.Setup(r => r.LogExecutionAsync(It.IsAny<ManualPaperTradeExecutionLog>())).Callback<ManualPaperTradeExecutionLog>(_logs.Add).Returns(Task.CompletedTask);
        return new ManualPaperTradeService(_repo.Object, Mock.Of<IMarketHoursService>(), NullLogger<ManualPaperTradeService>.Instance);
    }

    private static ManualPaperAutoExitPositionDto LongPosition(DateTime? checkedUntil = null) => new()
    {
        Id = 7, AccountId = 1, Symbol = "INFY", Side = TradeSide.BUY, Quantity = 10, AverageEntryPrice = 100m,
        StopLoss = 97m, TakeProfit = 105m, TrailingSlPct = 2m,
        OpenedAt = IstToUtc(new DateTime(2026, 10, 7, 10, 0, 30)), ExitCheckedUntil = checkedUntil
    };

    [Fact]
    public async Task Pass_ClosesAtTheTarget_AndLogsAutoTarget()
    {
        var service = Service(LongPosition(), Candles((100m, 105.5m, 99.5m, 105m)));

        int closed = await service.ProcessAutoExitsAsync();

        Assert.Equal(1, closed);
        _repo.Verify(r => r.ClosePositionAsync(1, 7, 105m, It.Is<string>(s => s.StartsWith("Target Hit"))), Times.Once);
        Assert.Contains(_logs, l => l.ActionType == "AUTO_TARGET");
    }

    [Fact]
    public async Task Pass_ReadsOnlyCompletedCandlesAfterTheEntryMinute()
    {
        var service = Service(LongPosition(), Candles((100m, 100.5m, 99.5m, 100.2m)));

        await service.ProcessAutoExitsAsync();

        // Entry at 10:00:30 IST -> from the 10:01 candle; now 11:00 IST -> up to the 10:59 candle (the last completed one).
        _repo.Verify(r => r.GetCandlesAsync("INFY", FirstCandleUtc, IstToUtc(TradingDayIst.AddMinutes(-1))), Times.Once);
    }

    [Fact]
    public async Task Pass_ContinuesAfterTheLastCheckedCandle()
    {
        var service = Service(LongPosition(checkedUntil: IstToUtc(new DateTime(2026, 10, 7, 10, 30, 0))), Candles((100m, 100.5m, 99.5m, 100.2m)));

        await service.ProcessAutoExitsAsync();

        _repo.Verify(r => r.GetCandlesAsync("INFY", IstToUtc(new DateTime(2026, 10, 7, 10, 31, 0)), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Pass_NoHit_SavesTheTrailState_AndLogsActivation()
    {
        var service = Service(LongPosition(), Candles((100m, 102.5m, 99.8m, 102m)));

        int closed = await service.ProcessAutoExitsAsync();

        Assert.Equal(0, closed);
        _repo.Verify(r => r.UpdatePositionTrailAsync(7, 2m, 102.5m, 100.45m, FirstCandleUtc), Times.Once);
        _repo.Verify(r => r.ClosePositionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>()), Times.Never);
        Assert.Contains(_logs, l => l.ActionType == "TRAILING_SL_ACTIVATED");
    }

    [Fact]
    public async Task Pass_PositionOpenedThisMinute_HasNoCompletedCandleYet()
    {
        var position = LongPosition();
        position.OpenedAt = IstToUtc(TradingDayIst.AddSeconds(-20));   // 10:59:40 IST
        var service = Service(position, Candles((100m, 105.5m, 99.5m, 105m)));

        int closed = await service.ProcessAutoExitsAsync();

        Assert.Equal(0, closed);
        _repo.Verify(r => r.GetCandlesAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Never);
    }
}
