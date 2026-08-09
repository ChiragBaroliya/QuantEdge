using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Exceptions;

namespace QuantEdge.API.Middleware;

public class PaperTradingExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PaperTradingExceptionMiddleware> _logger;

    public PaperTradingExceptionMiddleware(RequestDelegate next, ILogger<PaperTradingExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (PaperTradingException ex)
        {
            _logger.LogWarning("Paper Trading Domain Exception [{ErrorCode}]: {Message}", ex.ErrorCode, ex.Message);
            await HandlePaperTradingExceptionAsync(context, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception encountered during request execution.");
            await HandleGenericExceptionAsync(context, ex);
        }
    }

    private static Task HandlePaperTradingExceptionAsync(HttpContext context, PaperTradingException exception)
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = exception switch
        {
            InsufficientFundsException => (int)HttpStatusCode.UnprocessableEntity,
            PositionNotFoundException => (int)HttpStatusCode.NotFound,
            OrderNotFoundException => (int)HttpStatusCode.NotFound,
            InvalidOrderException => (int)HttpStatusCode.BadRequest,
            _ => (int)HttpStatusCode.BadRequest
        };

        var problemDetails = new ProblemDetails
        {
            Status = context.Response.StatusCode,
            Title = GetUserFriendlyTitle(exception),
            Detail = exception.Message,
            Instance = context.Request.Path
        };

        problemDetails.Extensions["errorCode"] = exception.ErrorCode;
        problemDetails.Extensions["timestamp"] = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(problemDetails);
        return context.Response.WriteAsync(json);
    }

    private static Task HandleGenericExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = 500,
            Title = "An Unexpected System Error Occurred",
            Detail = "Something went wrong on our end. Please refresh or try placing your paper trade again.",
            Instance = context.Request.Path
        };

        problemDetails.Extensions["errorCode"] = "INTERNAL_SERVER_ERROR";
        problemDetails.Extensions["timestamp"] = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(problemDetails);
        return context.Response.WriteAsync(json);
    }

    private static string GetUserFriendlyTitle(PaperTradingException ex) => ex switch
    {
        InsufficientFundsException => "Insufficient Margin Funds",
        InvalidOrderException => "Invalid Trade Parameters",
        PositionNotFoundException => "Position Not Found",
        OrderNotFoundException => "Order Not Found",
        _ => "Order Execution Alert"
    };
}
