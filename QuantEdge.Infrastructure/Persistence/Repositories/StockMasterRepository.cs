using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using System.IO;
using ClosedXML.Excel;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

/// <summary>
/// High-performance Dapper repository implementation for querying stock master records
/// using PostgreSQL stored functions.
/// </summary>
public class StockMasterRepository : IStockMasterRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ICacheService? _cacheService;

    static StockMasterRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public StockMasterRepository(IDbConnectionFactory connectionFactory, ICacheService? cacheService = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _cacheService = cacheService;
    }

    /// <summary>
    /// Retrieves all active stock symbols and instrument tokens using sp_get_active_stocks.
    /// Uses Memory Cache (TTL: 24 hours) for high-performance reads during market sessions.
    /// </summary>
    public async Task<IEnumerable<StockMaster>> GetActiveStocksAsync()
    {
        string cacheKey = "stock_master_active_stocks";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<IEnumerable<StockMaster>>(cacheKey);
            if (cached != null) return cached;
        }

        using var connection = _connectionFactory.CreateConnection();
        var result = (await connection.QueryAsync<StockMaster>(
            "SELECT * FROM sp_get_active_stocks();"
        )).ToList();

        if (_cacheService != null && result.Any())
        {
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromHours(24));
        }

        return result;
    }

    /// <summary>
    /// Retrieves a specific stock master record by symbol using sp_get_stock_by_symbol.
    /// Uses Memory Cache (TTL: 24 hours) for high-performance reads during market sessions.
    /// </summary>
    public async Task<StockMaster?> GetBySymbolAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        string cacheKey = $"stock_master_symbol_{symbol.ToUpper()}";
        if (_cacheService != null)
        {
            var cached = await _cacheService.GetAsync<StockMaster>(cacheKey);
            if (cached != null) return cached;
        }

        using var connection = _connectionFactory.CreateConnection();
        var result = await connection.QueryFirstOrDefaultAsync<StockMaster>(
            "SELECT * FROM sp_get_stock_by_symbol(@p_symbol);",
            new { p_symbol = symbol }
        );

        if (_cacheService != null && result != null)
        {
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromHours(24));
        }

        return result;
    }

    /// <summary>
    /// Retrieves all stock master records (active and inactive).
    /// </summary>
    public async Task<IEnumerable<StockMaster>> GetAllAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<StockMaster>(
            "SELECT * FROM stock_master ORDER BY symbol;"
        );
    }

    /// <summary>
    /// Updates the timeframe-specific history stored field for a stock master record.
    /// Invalidates affected memory cache keys.
    /// </summary>
    public async Task UpdateHistoryStoredAsync(int id, string timeframe, int? status)
    {
        using var connection = _connectionFactory.CreateConnection();
        string column = timeframe.ToLower() switch
        {
            "1m" => "is_histry_stored_1m",
            "5m" => "is_histry_stored_5m",
            "15m" => "is_histry_stored_15m",
            "60m" => "is_histry_stored_60m",
            "1d" => "is_histry_stored_1d",
            _ => throw new ArgumentException($"Invalid timeframe: {timeframe}")
        };
        await connection.ExecuteAsync(
            $"UPDATE stock_master SET {column} = @Status WHERE id = @Id;",
            new { Id = id, Status = status }
        );

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync("stock_master_active_stocks");
        }
    }

    /// <summary>
    /// Retrieves overall data coverage summary statistics via sp_get_data_coverage_summary.
    /// </summary>
    public async Task<CoverageSummaryDto> GetCoverageSummaryAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var result = await connection.QueryFirstOrDefaultAsync<CoverageSummaryDto>(
            "SELECT * FROM sp_get_data_coverage_summary();"
        );
        return result ?? new CoverageSummaryDto();
    }

    /// <summary>
    /// Retrieves paginated stock coverage data using sp_get_paginated_stock_coverage.
    /// </summary>
    public async Task<PaginatedCoverageResult> GetPaginatedCoverageAsync(string? search, string? statusFilter, string? historyFilter, int pageNumber, int pageSize)
    {
        using var connection = _connectionFactory.CreateConnection();
        var items = (await connection.QueryAsync<StockCoverageDto>(
            "SELECT * FROM sp_get_paginated_stock_coverage(@p_search, @p_status_filter, @p_history_filter, @p_page_number, @p_page_size);",
            new {
                p_search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                p_status_filter = string.IsNullOrWhiteSpace(statusFilter) ? null : statusFilter.Trim(),
                p_history_filter = string.IsNullOrWhiteSpace(historyFilter) ? null : historyFilter.Trim(),
                p_page_number = pageNumber < 1 ? 1 : pageNumber,
                p_page_size = pageSize < 1 ? 25 : pageSize
            }
        )).ToList();

        int totalCount = items.FirstOrDefault()?.TotalRecords ?? 0;

        return new PaginatedCoverageResult
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Updates a stock's active status and timeframe history stored flags using sp_update_stock_coverage_flags.
    /// </summary>
    public async Task UpdateStockCoverageFlagsAsync(UpdateStockCoverageRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_update_stock_coverage_flags(@p_id, @p_is_active, @p_histry_1m, @p_histry_5m, @p_histry_15m, @p_histry_60m, @p_histry_1d);",
            new {
                p_id = request.Id,
                p_is_active = request.IsActive,
                p_histry_1m = request.Get1mValue(),
                p_histry_5m = request.Get5mValue(),
                p_histry_15m = request.Get15mValue(),
                p_histry_60m = request.Get60mValue(),
                p_histry_1d = request.Get1dValue()
            }
        );
    }

    /// <summary>
    /// Deletes a stock master record and its associated market candles using sp_delete_stock_master.
    /// </summary>
    public async Task DeleteStockAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_delete_stock_master(@p_id);",
            new { p_id = id }
        );
    }

    /// <summary>
    /// Deletes multiple stock master records and associated market candles using sp_bulk_delete_stock_master.
    /// </summary>
    public async Task BulkDeleteStocksAsync(IEnumerable<int> ids)
    {
        if (ids == null || !ids.Any()) return;

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "SELECT sp_bulk_delete_stock_master(@p_ids);",
            new { p_ids = ids.ToArray() }
        );
    }

    /// <summary>
    /// Generates and exports Excel report (.xlsx) of stock coverage data based on search and filter criteria.
    /// </summary>
    public async Task<byte[]> ExportStockCoverageToExcelAsync(string? search, string? statusFilter, string? historyFilter)
    {
        var result = await GetPaginatedCoverageAsync(search, statusFilter, historyFilter, pageNumber: 1, pageSize: 100000);
        var items = result.Items ?? Enumerable.Empty<StockCoverageDto>();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Stock Coverage");

        // 1. Report Title Banner
        worksheet.Cell("A1").Value = "QuantEdge — Stock Data Coverage Report";
        worksheet.Cell("A1").Style.Font.Bold = true;
        worksheet.Cell("A1").Style.Font.FontSize = 16;
        worksheet.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#1E293B");

        string generatedTimeStr = DateTime.Now.ToString("dd-MMM-yyyy HH:mm:ss");
        worksheet.Cell("A2").Value = $"Generated: {generatedTimeStr} | Status Filter: {statusFilter ?? "All"} | History Filter: {historyFilter ?? "All"} | Search: {search ?? "None"} | Total Records: {result.TotalCount}";
        worksheet.Cell("A2").Style.Font.Italic = true;
        worksheet.Cell("A2").Style.Font.FontSize = 10;
        worksheet.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#64748B");

        // 2. Table Headers
        string[] headers = new[]
        {
            "#", "Symbol", "Company Name", "Exchange", "Instrument Token", "Status",
            "1m History", "5m History", "15m History", "60m History", "1D History",
            "1D Candle Count", "60m Candle Count", "Last Candle Sync Date"
        };

        int headerRow = 4;
        for (int col = 0; col < headers.Length; col++)
        {
            var cell = worksheet.Cell(headerRow, col + 1);
            cell.Value = headers[col];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 11;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
            cell.Style.Alignment.Horizontal = (col == 0 || col >= 11) ? XLAlignmentHorizontalValues.Right : XLAlignmentHorizontalValues.Left;
            if (col == 1 || col == 3 || col == 5 || (col >= 6 && col <= 10)) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        worksheet.Row(headerRow).Height = 26;

        // 3. Data Rows
        int currentRow = headerRow + 1;
        int rowIndex = 1;

        foreach (var item in items)
        {
            worksheet.Cell(currentRow, 1).Value = rowIndex++;
            worksheet.Cell(currentRow, 2).Value = item.Symbol;
            worksheet.Cell(currentRow, 3).Value = item.Name ?? item.Symbol;
            worksheet.Cell(currentRow, 4).Value = item.Exchange ?? "NSE";
            worksheet.Cell(currentRow, 5).Value = item.InstrumentToken;
            worksheet.Cell(currentRow, 6).Value = item.IsActive ? "Active" : "Inactive";
            
            worksheet.Cell(currentRow, 7).Value = FormatHistoryStatus(item.IsHistryStored1m);
            worksheet.Cell(currentRow, 8).Value = FormatHistoryStatus(item.IsHistryStored5m);
            worksheet.Cell(currentRow, 9).Value = FormatHistoryStatus(item.IsHistryStored15m);
            worksheet.Cell(currentRow, 10).Value = FormatHistoryStatus(item.IsHistryStored60m);
            worksheet.Cell(currentRow, 11).Value = FormatHistoryStatus(item.IsHistryStored1d);

            worksheet.Cell(currentRow, 12).Value = item.Count1d;
            worksheet.Cell(currentRow, 13).Value = item.Count60m;
            worksheet.Cell(currentRow, 14).Value = item.LastCandleDate.HasValue 
                ? item.LastCandleDate.Value.ToString("yyyy-MM-dd HH:mm:ss") 
                : "No Sync";

            // Zebra striping
            if (currentRow % 2 == 0)
            {
                worksheet.Row(currentRow).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            }

            // Cell formatting
            worksheet.Cell(currentRow, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            worksheet.Cell(currentRow, 2).Style.Font.Bold = true;
            worksheet.Cell(currentRow, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            worksheet.Cell(currentRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            // Status Badge Formatting
            var statusCell = worksheet.Cell(currentRow, 6);
            statusCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            statusCell.Style.Font.Bold = true;
            if (item.IsActive)
            {
                statusCell.Style.Font.FontColor = XLColor.FromHtml("#16A34A"); // Active green
            }
            else
            {
                statusCell.Style.Font.FontColor = XLColor.FromHtml("#DC2626"); // Inactive red
            }

            // History status alignment
            for (int hCol = 7; hCol <= 11; hCol++)
            {
                worksheet.Cell(currentRow, hCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            worksheet.Cell(currentRow, 12).Style.NumberFormat.Format = "#,##0";
            worksheet.Cell(currentRow, 12).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            worksheet.Cell(currentRow, 13).Style.NumberFormat.Format = "#,##0";
            worksheet.Cell(currentRow, 13).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            worksheet.Cell(currentRow, 14).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            currentRow++;
        }

        // Apply gridlines & auto-fit columns
        worksheet.ShowGridLines = true;
        if (currentRow > headerRow + 1)
        {
            worksheet.Columns().AdjustToContents(headerRow, currentRow - 1);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string FormatHistoryStatus(int? val)
    {
        return val switch
        {
            1 => "Stored",
            0 => "Missing",
            _ => "Not Stored"
        };
    }
}



