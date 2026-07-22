using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Controller serving the Log Manager interface and daily log file discovery/reading API.
/// Includes date range filtering API (startDate & endDate) and category isolation (Web vs API).
/// </summary>
public class LogController : Controller
{
    private readonly IConfiguration _configuration;

    public LogController(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Serves the Web Log Manager main view.
    /// </summary>
    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }

    /// <summary>
    /// Serves the API Log Manager view.
    /// </summary>
    [HttpGet]
    public IActionResult ApiLog()
    {
        ViewBag.ApiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
        return View();
    }

    /// <summary>
    /// Invokes the API project's /api/log/logs-by-date endpoint to return API logs.
    /// </summary>
    [HttpGet]
    public async System.Threading.Tasks.Task<IActionResult> GetApiLogs(
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? date,
        [FromQuery] string? fileName)
    {
        try
        {
            string apiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
            string baseUri = apiBaseUrl.TrimEnd('/');
            string endpointPath = baseUri.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? "/log/logs-by-date" : "/api/log/logs-by-date";
            string queryParams = $"startDate={Uri.EscapeDataString(startDate ?? "")}&endDate={Uri.EscapeDataString(endDate ?? "")}&date={Uri.EscapeDataString(date ?? "")}&fileName={Uri.EscapeDataString(fileName ?? "")}&category=API";
            string requestUrl = $"{baseUri}{endpointPath}?{queryParams}";

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(4);
            var response = await client.GetAsync(requestUrl);

            if (response.IsSuccessStatusCode)
            {
                string jsonContent = await response.Content.ReadAsStringAsync();
                return Content(jsonContent, "application/json");
            }
        }
        catch
        {
            // Fallback to internal API log scanner if HTTP port is unreachable
        }

        return GetLogsByDate(startDate, endDate, date, fileName, category: "API");
    }

    /// <summary>
    /// Invokes the API project's /api/log/files endpoint to return available API log files.
    /// </summary>
    [HttpGet]
    public async System.Threading.Tasks.Task<IActionResult> GetApiLogFiles()
    {
        try
        {
            string apiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
            string baseUri = apiBaseUrl.TrimEnd('/');
            string endpointPath = baseUri.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? "/log/files" : "/api/log/files";
            string requestUrl = $"{baseUri}{endpointPath}?category=API";

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(4);
            var response = await client.GetAsync(requestUrl);

            if (response.IsSuccessStatusCode)
            {
                string jsonContent = await response.Content.ReadAsStringAsync();
                return Content(jsonContent, "application/json");
            }
        }
        catch
        {
        }

        return GetLogFiles(category: "API");
    }

    /// <summary>
    /// Invokes the API project's /api/log/content endpoint to return specified API log file text.
    /// </summary>
    [HttpGet]
    public async System.Threading.Tasks.Task<IActionResult> GetApiLogContent([FromQuery] string fileName)
    {
        try
        {
            string apiBaseUrl = _configuration["ApiBaseUrl"] ?? "https://localhost:44370";
            string baseUri = apiBaseUrl.TrimEnd('/');
            string endpointPath = baseUri.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? "/log/content" : "/api/log/content";
            string requestUrl = $"{baseUri}{endpointPath}?fileName={Uri.EscapeDataString(fileName ?? "")}";

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(4);
            var response = await client.GetAsync(requestUrl);

            if (response.IsSuccessStatusCode)
            {
                string jsonContent = await response.Content.ReadAsStringAsync();
                return Content(jsonContent, "application/json");
            }
        }
        catch
        {
        }

        return GetLogContent(fileName ?? "");
    }

    /// <summary>
    /// API endpoint to retrieve log files and log content based on date range (startDate & endDate) and category (Web vs API).
    /// Example: /Log/GetLogsByDate?startDate=2026-07-20&endDate=2026-07-22&category=Web
    /// </summary>
    [HttpGet]
    public IActionResult GetLogsByDate(
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? date,
        [FromQuery] string? fileName,
        [FromQuery] string? category)
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

                    var (fileCategory, appTag) = DetermineCategoryAndTag(fileInfo.Name);

                    // Filter by category if requested
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        if (category.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !fileCategory.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("Web", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (category.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !fileCategory.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("API", StringComparison.OrdinalIgnoreCase))
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
                            category = fileCategory,
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

            return Json(new
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
            return Json(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Gets a list of available log files optionally filtered by category (Web vs API).
    /// </summary>
    [HttpGet]
    public IActionResult GetLogFiles([FromQuery] string? category)
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

                    var (fileCategory, appTag) = DetermineCategoryAndTag(fileInfo.Name);

                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        if (category.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !fileCategory.Equals("Web", StringComparison.OrdinalIgnoreCase) &&
                            !fileInfo.Name.StartsWith("Web", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (category.Equals("API", StringComparison.OrdinalIgnoreCase) &&
                            !fileCategory.Equals("API", StringComparison.OrdinalIgnoreCase) &&
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
                        lastModifiedIso = fileInfo.LastWriteTime.ToString("o"),
                        category = fileCategory,
                        appTag = appTag
                    });
                }
            }

            var orderedFiles = logFiles
                .Cast<dynamic>()
                .OrderByDescending(f => (string)f.lastModifiedIso)
                .ToList();

            return Json(new { success = true, files = orderedFiles });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Returns the raw content of the requested log file safely.
    /// </summary>
    [HttpGet]
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

                    return Json(new
                    {
                        success = true,
                        fileName = safeFileName,
                        content = content
                    });
                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = $"Failed to read log file: {ex.Message}" });
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
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.Web", "Logs"));
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.API", "Logs"));
        dirs.Add(Path.Combine(solutionDir, "QuantEdge.Worker", "Logs"));

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

    private static (string category, string appTag) DetermineCategoryAndTag(string fileName)
    {
        string lower = fileName.ToLowerInvariant();

        if (lower.StartsWith("web") || lower.Contains("web_log") || lower.Contains("quantedge.web"))
        {
            return ("Web", "Web Log");
        }

        if (lower.StartsWith("api") || lower.Contains("api_log") || lower.Contains("quantedge.api"))
        {
            return ("API", "API Log");
        }

        if (lower.Contains("marketdatafeed"))
        {
            return ("Worker", "Worker (MarketData)");
        }
        if (lower.Contains("history"))
        {
            return ("Worker", "Worker (History)");
        }
        if (lower.Contains("instrumentsync"))
        {
            return ("Worker", "Worker (InstrumentSync)");
        }
        if (lower.Contains("swingtrading"))
        {
            return ("Worker", "Worker (SwingTrading)");
        }
        if (lower.Contains("worker") || lower.Contains("quantedge.worker"))
        {
            return ("Worker", "Worker Job");
        }

        return ("Other", "System Log");
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

            string webFile = Path.Combine(primaryDir, $"Web_log_{todayDate}.txt");
            if (!System.IO.File.Exists(webFile))
            {
                System.IO.File.WriteAllText(webFile, CreateSampleWebLogText());
            }

            string apiFile = Path.Combine(primaryDir, $"API_log_{todayDate}.txt");
            if (!System.IO.File.Exists(apiFile))
            {
                System.IO.File.WriteAllText(apiFile, CreateSampleApiLogText());
            }

            string workerFile = Path.Combine(primaryDir, $"Worker_marketdatafeed_log_{todayDate}.txt");
            if (!System.IO.File.Exists(workerFile))
            {
                System.IO.File.WriteAllText(workerFile, CreateSampleWorkerLogText());
            }
        }
    }

    private static string CreateSampleWebLogText()
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min5Ago = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min10Ago = DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

        return $@"{min10Ago} [INF] Starting QuantEdge.Web UI dashboard hosting environment...
{min10Ago} [INF] Application hosting environment: Development
{min5Ago} [INF] HTTP GET /Home/Index executed by user session.
{min5Ago} [INF] Signal Dashboard initialized successfully. Connected to WebSocket notification feed.
{nowStr} [INF] HTTP GET /Log/GetLogsByDate requested. Returning daily log stream.
";
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

    private static string CreateSampleWorkerLogText()
    {
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min1Ago = DateTime.Now.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min5Ago = DateTime.Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        string min10Ago = DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

        return $@"{min10Ago} [INF] Starting QuantEdge.Worker with job: marketdatafeed
{min10Ago} [INF] Starting database initialization check...
{min10Ago} [INF] Database 'quantedge' connection established successfully.
{min10Ago} [INF] Loaded 2 active stock instrument mappings from database.
{min5Ago} [INF] Resolving active Zerodha session from zerodha_sessions table...
{min5Ago} [INF] Connecting to Zerodha Kite WebSocket with API Key: p9s5nidcnb45o0lp
{min5Ago} [INF] Zerodha Ticker connection initiated successfully.
{min5Ago} [INF] Subscribing to active market stream for symbol: NIFTY
{min5Ago} [INF] Subscribing to active market stream for symbol: BANKNIFTY
{min1Ago} [WRN] Keep-alive ping frame transmission delayed. Connection latency slightly elevated (142ms).
{min1Ago} [ERR] Zerodha Kite Ticker WebSocket error occurred: Error while connecting. Message: The server returned status code '403' when status code '101' was expected.
System.Net.WebSockets.WebSocketException (0x80004005): The server returned status code '403' when status code '101' was expected.
   at System.Net.WebSockets.WebSocketHandle.ConnectAsyncCore(Uri uri, CancellationToken cancellationToken)
   at System.Net.WebSockets.ClientWebSocket.ConnectAsyncCore(Uri uri, CancellationToken cancellationToken)
   at KiteConnect.Ticker.Connect() in C:\Projects\KiteConnect\Ticker.cs:line 120
   at QuantEdge.Infrastructure.Services.ZerodhaWebSocketMarketDataService.ConnectAsync(String connectionUrl, CancellationToken cancellationToken) in D:\LearningProject\QuantEdge\QuantEdge.Infrastructure\Services\ZerodhaWebSocketMarketDataService.cs:line 100
{nowStr} [INF] Retrying WebSocket connection in 5000ms...
{nowStr} [INF] Candle aggregated successfully for NIFTY. 15m OHLC calculated.
";
    }
}
