using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface IStockVerdictService
{
    /// <summary>
    /// One multi-timeframe verdict (BUY / WAIT / AVOID, or HOLD when owned) for the Signal Dashboard,
    /// from the same SwingDecisionEngine evaluation and candles the auto-trading scan uses.
    /// </summary>
    Task<StockVerdictDto> GetVerdictAsync(string symbol, int userId = 1);
}
