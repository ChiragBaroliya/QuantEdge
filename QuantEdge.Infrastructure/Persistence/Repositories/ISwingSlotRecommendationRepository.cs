using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface ISwingSlotRecommendationRepository
{
    Task SaveSlotRecommendationsAsync(DateTime scanDate, DateTime slotTime, string slotLabel, IEnumerable<SwingStockSignalDto> signals, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SwingScanSlotDto>> GetScanSlotsAsync(DateTime scanDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SwingStockSignalDto>> GetSlotRecommendationsAsync(DateTime scanDate, string slotLabel, CancellationToken cancellationToken = default);
}
