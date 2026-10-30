using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using QuantEdge.Infrastructure.Services;
using QuantEdge.Worker.Workers;
using Xunit;

namespace QuantEdge.Tests;

public class NseBhavcopyWorkerTests
{
    private static readonly DateTime Day = new(2026, 10, 26);
    private static readonly DateTime Afternoon = new(2026, 10, 30, 17, 10, 0);
    private static readonly IReadOnlyList<NseBhavcopyRow> Rows = new[] { new NseBhavcopyRow { TradeDate = Day, Symbol = "TCS", Series = "EQ", Close = 100m } };

    private readonly Mock<INseBhavcopyService> _service = new();
    private readonly Mock<ICorporateActionService> _corporate = new();
    private readonly Mock<IBrokerApiEventRecorder> _recorder = new();
    private readonly List<(string Operation, string Message)> _alerts = new();

    public NseBhavcopyWorkerTests()
    {
        _service.Setup(s => s.DownloadAsync(Day, It.IsAny<CancellationToken>())).ReturnsAsync(Rows);
        _service.Setup(s => s.StoreAsync(Day, Rows)).ReturnsAsync(new BhavcopyLoadResult(Day, 1, 1));
        _corporate.Setup(c => c.DetectAndApplyAsync(Day)).ReturnsAsync(Array.Empty<CorporateAction>());
        _recorder.Setup(r => r.RecordFailure(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string>()))
            .Callback((string _, string operation, string? message, int? _, string? _, int? _, string _) => _alerts.Add((operation, message!)));
    }

    private Task<bool> Run(bool isToday = false, DateTime? now = null) =>
        NseBhavcopyWorker.ProcessDayAsync(Day, isToday, now ?? Afternoon, _service.Object, _corporate.Object, _recorder.Object, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task MissingDay_IsStoredThenChecked()
    {
        Assert.True(await Run());

        _service.Verify(s => s.StoreAsync(Day, Rows), Times.Once);
        _corporate.Verify(c => c.DetectAndApplyAsync(Day), Times.Once);
        _service.Verify(s => s.MarkCorporateActionCheckDoneAsync(Day), Times.Once);
        Assert.Empty(_alerts);
    }

    [Fact]
    public async Task FinishedDay_IsNotTouched()
    {
        _service.Setup(s => s.IsLoadedAsync(Day)).ReturnsAsync(true);
        _service.Setup(s => s.IsCorporateActionCheckDoneAsync(Day)).ReturnsAsync(true);

        Assert.True(await Run());

        _service.Verify(s => s.DownloadAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _corporate.Verify(c => c.DetectAndApplyAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task DownloadFailure_IsReportedAsADownload_AndStopsTheLaterDays()
    {
        _service.Setup(s => s.DownloadAsync(Day, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("403 Forbidden"));

        Assert.False(await Run());

        var alert = Assert.Single(_alerts);
        Assert.Equal("bhavcopy", alert.Operation);
        Assert.Contains("download for 26-Oct-2026 failed: 403 Forbidden", alert.Message);
        _corporate.Verify(c => c.DetectAndApplyAsync(It.IsAny<DateTime>()), Times.Never);
    }

    // The original alert: a database timeout reported as a failed download.
    [Fact]
    public async Task DatabaseFailure_IsReportedAsADatabaseFailure_WithTheRealReason()
    {
        _service.Setup(s => s.StoreAsync(Day, Rows))
            .ThrowsAsync(new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")));

        Assert.False(await Run());

        var alert = Assert.Single(_alerts);
        Assert.Equal("bhavcopy database", alert.Operation);
        Assert.Contains("downloaded, but saving it to the database failed", alert.Message);
        Assert.Contains("Timeout during reading attempt", alert.Message);
        Assert.DoesNotContain("download for", alert.Message);
        _corporate.Verify(c => c.DetectAndApplyAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task FailedSplitCheck_IsRetried_WithoutDownloadingAgain()
    {
        _corporate.SetupSequence(c => c.DetectAndApplyAsync(Day))
            .ThrowsAsync(new NpgsqlException("Exception while reading from stream"))
            .ReturnsAsync(Array.Empty<CorporateAction>());

        Assert.False(await Run());
        Assert.Equal("corporate action", Assert.Single(_alerts).Operation);
        _service.Verify(s => s.MarkCorporateActionCheckDoneAsync(Day), Times.Never);

        // Next cycle: the file is stored now, and the check still hasn't finished.
        _service.Setup(s => s.IsLoadedAsync(Day)).ReturnsAsync(true);
        Assert.True(await Run());

        _corporate.Verify(c => c.DetectAndApplyAsync(Day), Times.Exactly(2));
        _service.Verify(s => s.MarkCorporateActionCheckDoneAsync(Day), Times.Once);
        _service.Verify(s => s.DownloadAsync(Day, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoFileFromNse_IsQuiet_AndLetsLaterDaysRun()
    {
        _service.Setup(s => s.DownloadAsync(Day, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<NseBhavcopyRow>?)null);

        Assert.True(await Run());

        Assert.Empty(_alerts);
        _corporate.Verify(c => c.DetectAndApplyAsync(It.IsAny<DateTime>()), Times.Never);
        _service.Verify(s => s.MarkCorporateActionCheckDoneAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task TodaysFileMissingAfter10pm_RaisesAWarning()
    {
        _service.Setup(s => s.DownloadAsync(Day, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<NseBhavcopyRow>?)null);

        Assert.True(await Run(isToday: true, now: new DateTime(2026, 10, 26, 22, 30, 0)));

        Assert.Contains("not published by 22:00", Assert.Single(_alerts).Message);
    }

    [Fact]
    public void IstDayBounds_CoverExactlyOneIstDay()
    {
        var (start, end) = NseBhavcopyService.IstDayBoundsUtc(Day);

        Assert.Equal(new DateTime(2026, 10, 25, 18, 30, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 10, 26, 18, 30, 0, DateTimeKind.Utc), end);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }
}
