using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Manages thread-safe connection, transmission, and streaming of WebSocket data using ClientWebSocket.
/// </summary>
public class WebSocketConnectionManager : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly ILogger<WebSocketConnectionManager> _logger;

    /// <summary>
    /// Gets whether the WebSocket state is active and open.
    /// </summary>
    public bool IsOpen => _webSocket?.State == WebSocketState.Open;

    public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Connects to a remote WebSocket URL. Resets any previous failed state.
    /// </summary>
    public async Task ConnectAsync(string connectionUrl, CancellationToken cancellationToken)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsOpen)
            {
                _logger.LogWarning("WebSocket is already open. Gracefully closing active connection first.");
                await DisconnectInternalAsync(cancellationToken);
            }

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            
            // Modern .NET settings: 30-second keep alive handshake
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            _logger.LogInformation("Opening WebSocket stream to: {WebSocketUrl}", connectionUrl);
            await _webSocket.ConnectAsync(new Uri(connectionUrl), cancellationToken);
            _logger.LogInformation("WebSocket connection established successfully to: {WebSocketUrl}", connectionUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish WebSocket connection to: {WebSocketUrl}", connectionUrl);
            throw;
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    /// <summary>
    /// Closes the active WebSocket connection gracefully.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            await DisconnectInternalAsync(cancellationToken);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    private async Task DisconnectInternalAsync(CancellationToken cancellationToken)
    {
        if (_webSocket == null)
        {
            return;
        }

        if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
        {
            _logger.LogInformation("Gracefully shutting down active WebSocket connection...");
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated closure", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to gracefully close WebSocket. Forcing close.");
                _webSocket.Abort();
            }
        }
        else
        {
            _webSocket.Abort();
        }

        _logger.LogInformation("WebSocket connection disconnected.");
    }

    /// <summary>
    /// Thread-safely sends a text message payload over the WebSocket connection.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        if (!IsOpen || _webSocket == null)
        {
            throw new InvalidOperationException("WebSocket connection is not open.");
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);

        await _sendSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transmitting WebSocket payload.");
            throw;
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    /// <summary>
    /// Streams incoming text payloads from the active socket as an asynchronous stream.
    /// </summary>
    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (IsOpen && !cancellationToken.IsCancellationRequested)
        {
            var stringBuilder = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                try
                {
                    result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during receiving stream chunk from WebSocket connection.");
                    yield break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Received Close frame from remote WebSocket host. Reason: {CloseStatusDescription}", result.CloseStatusDescription);
                    await DisconnectAsync(cancellationToken);
                    yield break;
                }

                var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                stringBuilder.Append(chunk);

            } while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested);

            if (stringBuilder.Length > 0)
            {
                yield return stringBuilder.ToString();
            }
        }
    }

    /// <summary>
    /// Disposes connection and synchronization resources.
    /// </summary>
    public void Dispose()
    {
        _webSocket?.Dispose();
        _sendSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
