using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Sits on the Kite Connect HttpClient (ZerodhaKiteBrokerService.HttpClientName) so EVERY REST call - order
/// placement, order status, cancel, positions, holdings, quotes, margins - reports a failure to the header bell
/// from one place. Non-2xx responses (including 429 rate limits) and network exceptions are recorded; the
/// response or exception still reaches the caller unchanged. Adds no Kite calls.
/// </summary>
public sealed class KiteApiFailureHandler : DelegatingHandler
{
    private static readonly Regex IdSegment = new(@"/\d{6,}(?=/|$)", RegexOptions.Compiled);

    private readonly IBrokerApiEventRecorder _recorder;

    public KiteApiFailureHandler(IBrokerApiEventRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string operation = $"{request.Method} {IdSegment.Replace(request.RequestUri?.AbsolutePath ?? "?", "/{id}")}";
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _recorder.RecordFailure(BrokerApiSource.Rest, operation, $"Network error - Zerodha not reached: {ex.Message}");
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadKiteMessageAsync(response);
            string message = response.StatusCode == HttpStatusCode.TooManyRequests
                ? $"RATE LIMITED by Zerodha (HTTP 429) - too many requests. {detail}".Trim()
                : $"HTTP {(int)response.StatusCode}: {detail}".Trim();
            _recorder.RecordFailure(BrokerApiSource.Rest, operation, message, (int)response.StatusCode);
        }

        return response;
    }

    // Kite error bodies are {"status":"error","message":"...","error_type":"..."}. Buffered so the caller can still read it.
    private static async Task<string> ReadKiteMessageAsync(HttpResponseMessage response)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync();
            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            string? msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? type = doc.RootElement.TryGetProperty("error_type", out var t) ? t.GetString() : null;
            return string.IsNullOrWhiteSpace(type) ? msg ?? string.Empty : $"{type}: {msg}";
        }
        catch
        {
            return response.ReasonPhrase ?? string.Empty;
        }
    }
}
