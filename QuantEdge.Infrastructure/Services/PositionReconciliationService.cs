using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>Our open real position vs what Zerodha holds for the same stock.</summary>
public sealed class PositionReconciliationRow
{
    public string Symbol { get; set; } = string.Empty;
    public int OurQuantity { get; set; }
    public int BrokerQuantity { get; set; }
    public decimal OurAveragePrice { get; set; }
    public decimal BrokerAveragePrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal OurGrossPnl { get; set; }
    public decimal BrokerPnl { get; set; }
    public decimal EstimatedCharges { get; set; }
    public decimal OurNetPnl => OurGrossPnl - EstimatedCharges;
    /// <summary>MATCH, or the first field that differs: QUANTITY / AVERAGE_PRICE / MISSING_AT_ZERODHA.</summary>
    public string Status { get; set; } = "MATCH";
    public string Explanation { get; set; } = string.Empty;
}

public sealed class PositionReconciliationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public List<PositionReconciliationRow> Rows { get; set; } = new();
}

public interface IPositionReconciliationService
{
    /// <summary>
    /// Compares open real positions with Zerodha holdings + positions (2 Kite calls). With <paramref name="persistIssues"/>
    /// mismatches are stored in data_quality_issues and summarised in the header bell (the daily job does this).
    /// </summary>
    Task<PositionReconciliationResult> CompareAsync(int userId, bool persistIssues = false);
}

public class PositionReconciliationService : IPositionReconciliationService
{
    private const decimal AvgPriceTolerancePct = 0.5m;

    private readonly IRealTradingRepository _realTradingRepository;
    private readonly IZerodhaKiteBrokerService _brokerService;
    private readonly IDataQualityRepository _dataQualityRepository;
    private readonly IBrokerApiEventRecorder _recorder;

    public PositionReconciliationService(IRealTradingRepository realTradingRepository, IZerodhaKiteBrokerService brokerService,
        IDataQualityRepository dataQualityRepository, IBrokerApiEventRecorder recorder)
    {
        _realTradingRepository = realTradingRepository ?? throw new ArgumentNullException(nameof(realTradingRepository));
        _brokerService = brokerService ?? throw new ArgumentNullException(nameof(brokerService));
        _dataQualityRepository = dataQualityRepository ?? throw new ArgumentNullException(nameof(dataQualityRepository));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public async Task<PositionReconciliationResult> CompareAsync(int userId, bool persistIssues = false)
    {
        var positions = (await _realTradingRepository.GetOpenPositionsAsync(userId)).ToList();
        if (positions.Count == 0) return new PositionReconciliationResult { Success = true, Message = "No open real positions." };

        var holdingsTask = _brokerService.GetLiveHoldingsAsync(userId);
        var brokerPositionsTask = _brokerService.GetLivePositionsAsync(userId);
        await Task.WhenAll(holdingsTask, brokerPositionsTask);
        var holdings = holdingsTask.Result;
        var brokerPositions = brokerPositionsTask.Result;
        if (!holdings.Success || !brokerPositions.Success)
        {
            return new PositionReconciliationResult
            {
                Success = false,
                Message = $"Could not read Zerodha holdings/positions: {holdings.Message ?? brokerPositions.Message}"
            };
        }

        var result = new PositionReconciliationResult { Success = true };
        result.Rows = Compare(positions, holdings.Holdings ?? new List<ZerodhaHoldingDto>(), brokerPositions.Positions?.Net ?? new List<ZerodhaPositionItemDto>());

        if (persistIssues)
        {
            DateTime todayIst = DayChangeCalculator.IstDate(DateTime.UtcNow);
            var issues = result.Rows.Where(r => r.Status != "MATCH").Select(r => new DataQualityIssue
            {
                CheckDate = todayIst,
                CheckType = r.Status switch
                {
                    "QUANTITY" => "POSITION_QTY_VS_ZERODHA",
                    "AVERAGE_PRICE" => "POSITION_AVG_VS_ZERODHA",
                    _ => "POSITION_MISSING_AT_ZERODHA"
                },
                Symbol = r.Symbol,
                UserId = userId,
                Ours = r.Status == "AVERAGE_PRICE" ? r.OurAveragePrice : r.OurQuantity,
                External = r.Status == "AVERAGE_PRICE" ? r.BrokerAveragePrice : r.BrokerQuantity,
                DiffPct = r.Status == "AVERAGE_PRICE" && r.BrokerAveragePrice > 0m
                    ? Math.Round((r.OurAveragePrice - r.BrokerAveragePrice) / r.BrokerAveragePrice * 100m, 4)
                    : null,
                Details = r.Explanation
            }).ToList();
            await _dataQualityRepository.InsertAsync(issues);
            if (issues.Count > 0)
            {
                _recorder.RecordFailure(BrokerApiSource.Job, "position reconciliation",
                    $"{issues.Count} open position(s) don't match Zerodha ({string.Join(", ", issues.Select(i => i.Symbol))}). See the Reconciliation page.",
                    userId: userId, level: "warning");
            }
        }
        return result;
    }

    /// <summary>Pure comparison: holdings + T1 + today's net CNC position = Zerodha's quantity for the stock.</summary>
    public static List<PositionReconciliationRow> Compare(IEnumerable<RealPosition> ours, IReadOnlyList<ZerodhaHoldingDto> holdings,
        IReadOnlyList<ZerodhaPositionItemDto> brokerPositions)
    {
        var rows = new List<PositionReconciliationRow>();
        foreach (var p in ours)
        {
            var h = holdings.FirstOrDefault(x => string.Equals(x.TradingSymbol, p.Symbol, StringComparison.OrdinalIgnoreCase));
            var bp = brokerPositions.FirstOrDefault(x => string.Equals(x.TradingSymbol, p.Symbol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Product, "CNC", StringComparison.OrdinalIgnoreCase));

            int brokerQty = (h?.Quantity ?? 0) + (h?.T1Quantity ?? 0) + (bp?.Quantity ?? 0);
            decimal brokerAvg = h != null && h.AveragePrice > 0m ? h.AveragePrice : bp?.BuyPrice ?? 0m;
            decimal ltp = h?.LastPrice > 0m ? h.LastPrice : bp?.LastPrice ?? 0m;
            decimal grossPnl = ltp > 0m ? (ltp - p.AverageEntryPrice) * p.Quantity : 0m;

            var row = new PositionReconciliationRow
            {
                Symbol = p.Symbol,
                OurQuantity = p.Quantity,
                BrokerQuantity = brokerQty,
                OurAveragePrice = Math.Round(p.AverageEntryPrice, 2),
                BrokerAveragePrice = Math.Round(brokerAvg, 2),
                LastPrice = ltp,
                OurGrossPnl = Math.Round(grossPnl, 2),
                BrokerPnl = Math.Round((h?.Pnl ?? 0m) + (bp?.Pnl ?? 0m), 2),
                EstimatedCharges = ltp > 0m
                    ? Math.Round(ChargesCalculator.EstimateOpenPosition(p.AverageEntryPrice, ltp, p.Quantity, false, p.OpenedAt), 2)
                    : 0m
            };

            if (brokerQty <= 0)
            {
                row.Status = "MISSING_AT_ZERODHA";
                row.Explanation = $"QuantEdge shows {p.Quantity} {p.Symbol} open but Zerodha holds none - the position may have been sold outside QuantEdge, or the buy never completed.";
            }
            else if (brokerQty != p.Quantity)
            {
                row.Status = "QUANTITY";
                row.Explanation = brokerQty < p.Quantity
                    ? $"Zerodha holds {brokerQty}, QuantEdge {p.Quantity}: a partial fill or a sell outside QuantEdge."
                    : $"Zerodha holds {brokerQty}, QuantEdge {p.Quantity}: older shares of {p.Symbol} in the account, or a buy outside QuantEdge.";
            }
            else if (brokerAvg > 0m && Math.Abs(p.AverageEntryPrice - brokerAvg) / brokerAvg * 100m > AvgPriceTolerancePct)
            {
                row.Status = "AVERAGE_PRICE";
                row.Explanation = $"Average price differs (QuantEdge ₹{p.AverageEntryPrice:F2} vs Zerodha ₹{brokerAvg:F2}) - Zerodha blends older lots of the same stock, or the fill price was recorded from a quote.";
            }
            else
            {
                row.Explanation = "Quantity and average price match Zerodha.";
            }
            rows.Add(row);
        }
        return rows;
    }
}
