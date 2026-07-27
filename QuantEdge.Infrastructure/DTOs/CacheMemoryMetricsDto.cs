using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// DTO representing real-time process RAM memory and Market Data Cache statistics.
/// </summary>
public class CacheMemoryMetricsDto
{
    public double ProcessWorkingSetMB { get; set; }
    public double GcTotalMemoryMB { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int TotalCachedSymbols { get; set; }
    public int TotalCachedCandles { get; set; }
    public int TotalCachedIndicators { get; set; }
    public double EstimatedCacheMemoryMB { get; set; }
    public Dictionary<string, int> TimeframeCandleCounts { get; set; } = new();
}
