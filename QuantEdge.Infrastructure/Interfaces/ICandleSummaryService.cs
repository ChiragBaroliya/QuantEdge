using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface ICandleSummaryService
{
    Task<CandleSummaryResponseDto> GetCandleSummaryAsync(CandleSummaryFilterDto filter);
    Task<IEnumerable<string>> GetActiveSymbolsAsync();
}
