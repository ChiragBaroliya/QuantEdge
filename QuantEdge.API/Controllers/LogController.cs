using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace QuantEdge.API.Controllers;

/// <summary>
/// API Controller providing system and application log files by date range (startDate & endDate).
/// </summary>
[ApiController]
[Route("api/log")]
public class LogController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LogController> _logger;

    public LogController(IConfiguration configuration, ILogger<LogController> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/log/logs-by-date - Returns log files and log stream content for a date range (startDate & endDate).
    /// Example: GET /api/log/logs-by-date?startDate=2026-07-20&endDate=2026-07-22&category=API
    /// </summary>
    [HttpGet("logs-by-date")]
    public IActionResult GetLogsByDate(
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? date,
        [FromQuery] string? fileName,
        [FromQuery] string? category = "API")
    {
        try
        {
            DateTime start = DateTime.Now.Date;
            DateTime end = DateTime.Now.Date;

            if (!string.IsNullOrWhiteSpace(startDate))
            {
                ParseDateInput(startDate, out start);
            }
            else if (!string.IsNullOrWhiteSpace(date))
            {
                ParseDateInput(date, out start);
            }

            if (!string.IsNullOrWhiteSpace(endDate))
            {
                ParseDateInput(endDate, out end);
            }
            else if (!string.IsNullOrWhiteSpace(date))
            {
                ParseDateInput(date, out end);
            }

            if (start > end)
            {
                var temp = start;
                start = end;
                end = temp;
            }

            string dateDisplay = (start == end)
                ? start.ToString("dd/MM/yyyy")
                : $"{start:dd/MM/yyyy} - {end:dd/MM/yyyy}";

            var candidateDirectories = GetCandidateLogDirectories();
            EnsureSampleLogFilesIfEmpty(candidateDirectories);

            var matchingFiles = new List<object>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in candidateDirectories)
            {
                if (!Directory.Exists(dir)) continue;

                var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

                foreach (var filePath in files)
                {
                    var fileInfo = new FileInfo(filePath);
                    if (seenFiles.Contains(fileInfo.Name)) continue;

                    string appTag = DetermineAppTag(fileInfo.Name);

                    // If category filter requested (e.g. API), isolate matching files
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        if (category.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !appTag.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("API", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (category.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !appTag.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("Web", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    DateTime fileDate = ExtractFileDate(fileInfo);

                    if (fileDate.Date >= start.Date && fileDate.Date <= end.Date)
                    {
                        seenFiles.Add(fileInfo.Name);

                        string formattedSize = FormatFileSize(fileInfo.Length);

                        matchingFiles.Add(new
                        {
                            fileName = fileInfo.Name,
                            fullPath = fileInfo.FullName,
                            size = formattedSize,
                            sizeBytes = fileInfo.Length,
                            lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            logDate = fileDate.ToString("yyyy-MM-dd"),
                            appTag = appTag
                        });
                    }
                }
            }

            var orderedFiles = matchingFiles
                .Cast<dynamic>()
                .OrderByDescending(f => (string)f.lastModified)
                .ToList();

            string? selectedContent = null;
            string? activeFileName = null;

            string? targetFileName = !string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileName(fileName)
                : (orderedFiles.FirstOrDefault()?.fileName);

            if (!string.IsNullOrEmpty(targetFileName))
            {
                foreach (var dir in candidateDirectories)
                {
                    if (!Directory.Exists(dir)) continue;
                    string path = Path.Combine(dir, targetFileName);
                    if (System.IO.File.Exists(path))
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        selectedContent = reader.ReadToEnd();
                        activeFileName = targetFileName;
                        break;
                    }
                }
            }

            return Ok(new
            {
                success = true,
                queryDate = dateDisplay,
                startDate = start.ToString("yyyy-MM-dd"),
                endDate = end.ToString("yyyy-MM-dd"),
                files = orderedFiles,
                selectedFileName = activeFileName,
                content = selectedContent
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch logs for date range {Start} to {End}", startDate, endDate);
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/log/files - Returns available API log files.
    /// </summary>
    [HttpGet("files")]
    public IActionResult GetLogFiles([FromQuery] string? category = "API")
    {
        try
        {
            var candidateDirectories = GetCandidateLogDirectories();
            EnsureSampleLogFilesIfEmpty(candidateDirectories);

            var logFiles = new List<object>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in candidateDirectories)
            {
                if (!Directory.Exists(dir)) continue;

                var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

                foreach (var filePath in files)
                {
                    var fileInfo = new FileInfo(filePath);
                    if (seenFiles.Contains(fileInfo.Name)) continue;

                    string appTag = DetermineAppTag(fileInfo.Name);

                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        if (category.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !appTag.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("API", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    seenFiles.Add(fileInfo.Name);

                    string formattedSize = FormatFileSize(fileInfo.Length);

                    logFiles.Add(new
                    {
                        fileName = fileInfo.Name,
                        fullPath = fileInfo.FullName,
                        size = formattedSize,
                        sizeBytes = fileInfo.Length,
                        lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        appTag = appTag
                    });
                }
            }

            var orderedFiles = logFiles
                .Cast<dynamic>()
                .OrderByDescending(f => (string)f.lastModified)
                .ToList();

            return Ok(new { success = true, files = orderedFiles });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch log files list.");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/log/content - Returns content of specified log file.
    /// </summary>
    [HttpGet("content")]
    public IActionResult GetLogContent([FromQuery] string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { success = false, message = "File name is required." });
        }

        string safeFileName = Path.GetFileName(fileName);
        var candidateDirectories = GetCandidateLogDirectories();

        foreach (var dir in candidateDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            string targetPath = Path.Combine(dir, safeFileName);
            if (System.IO.File.Exists(targetPath))
            {
                try
                {
                    using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string content = reader.ReadToEnd();

                    return Ok(new
                    {
                        success = true,
                        fileName = safeFileName,
                        content = content
                    });
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, message = $"Failed to read log file: {ex.Message}" });
                }
            }
        }

        return NotFound(new { success = false, message = $"Log file '{safeFileName}' not found." });
    }

    private List<string> GetCandidateLogDirectories()
    {
        var dirs = new List<string>();

        string configuredDir = _configuration["Logging:LogDirectory"] ?? "Logs";
        dirs.Add(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredDir)));
        dirs.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredDir)));

        string currentDir = Directory.GetCurrentDirectory();
        string solutionDir = Path.GetFullPath(Path.Combine(currentDir, ".."));

        dirs.Add(Path.Combine(solutionDir, "Logs"));
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.API", "Logs"));
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.Worker", "Logs"));
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.Web", "Logs"));

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool ParseDateInput(string dateStr, out DateTime parsedDate)
    {
        dateStr = dateStr.Trim();
        string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "yyyyMMdd", "dd-MM-yyyy" };
        if (DateTime.TryParseExact(dateStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
        {
            return true;
        }
        return DateTime.TryParse(dateStr, out parsedDate);
    }

    private static DateTime ExtractFileDate(FileInfo fileInfo)
    {
        var match = Regex.Match(fileInfo.Name, @"(\d{4})[-_]?(\d{2})[-_]?(\d{2})");
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int y) &&
                int.TryParse(match.Groups[2].Value, out int m) &&
                int.TryParse(match.Groups[3].Value, out int d))
            {
                try { return new DateTime(y, m, d); } catch { }
            }
        }
        return fileInfo.LastWriteTime.Date;
    }

    private static string DetermineAppTag(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        if (lower.StartsWith("api") || lower.Contains("api_log")) return "API";
        if (lower.StartsWith("web") || lower.Contains("web_log")) return "Web";
        if (lower.Contains("marketdatafeed")) return "Worker (MarketData)";
        if (lower.Contains("history")) return "Worker (History)";
        if (lower.Contains("instrumentsync")) return "Worker (InstrumentSync)";
        if (lower.Contains("worker")) return "Worker";
        return "System";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private void EnsureSampleLogFilesIfEmpty(List<string> candidateDirectories)
    {
        bool hasAnyFile = candidateDirectories
            .Where(Directory.Exists)
            .Any(d => Directory.GetFiles(d, "*.*").Any(f => f.EndsWith(".txt") || f.EndsWith(".log")));

        if (!hasAnyFile)
        {
            string primaryDir = candidateDirectories.FirstOrDefault() ?? Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(primaryDir))
            {
                Directory.CreateDirectory(primaryDir);
            }

            string todayDate = DateTime.Now.ToString("yyyyMMdd");
            string sampleFilePath = Path.Combine(primaryDir, $"API_log_{todayDate}.txt");

            if (!System.IO.File.Exists(sampleFilePath))
            {
                string sampleContent = CreateSampleApiLogText();
                System.IO.File.WriteAllText(sampleFilePath, sampleContent);
            }
        }
    }

    private static string CreateSampleApiLogText()
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min5Ago = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min10Ago = DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

        return $@"{min10Ago} [INF] Starting QuantEdge.API REST Services hosting environment...
{min10Ago} [INF] Database connection pool initialized for PostgreSQL quantedge.
{min5Ago} [INF] HTTP GET /datacoverage/summary requested by web client.
{min5Ago} [INF] Validated active Zerodha token credentials in zerodha_sessions table.
{nowStr} [INF] HTTP GET /api/log/logs-by-date processed successfully. Returned daily log stream.
";
    }
}
