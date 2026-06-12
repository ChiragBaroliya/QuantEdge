using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace QuantEdge.Infrastructure.Extensions;

/// <summary>
/// Service collection extensions providing centralized logging configuration using Serilog.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Configures and registers Serilog file and console logging dynamically for the calling application.
    /// </summary>
    /// <param name="services">The service collection container.</param>
    /// <param name="configuration">The configuration instance.</param>
    /// <param name="appName">The name of the calling application, used to name the log file.</param>
    /// <returns>The service collection container for chaining.</returns>
    public static IServiceCollection AddQuantEdgeLogging(
        this IServiceCollection services,
        IConfiguration configuration,
        string appName)
    {
        // Get and resolve log directory path
        string logDir = configuration["Logging:LogDirectory"] ?? "Logs";
        if (!Path.IsPathRooted(logDir))
        {
            logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logDir);
        }

        // Ensure the log folder is created if it does not exist
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        // Formulate the file name with the application name and current start DateTime
        string logFile = Path.Combine(logDir, $"{appName}_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        // Initialize Serilog configuration
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(logFile)
            .CreateLogger();

        // Register Serilog with the DI container
        services.AddSerilog();

        return services;
    }
}
