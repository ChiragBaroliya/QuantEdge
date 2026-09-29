using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Per-user favorite symbols - pinned to the top of the Signal Dashboard symbol dropdown.
/// </summary>
[ApiController]
[Route("favorites")]
public class FavoriteSymbolController : ControllerBase
{
    private readonly IFavoriteSymbolRepository _repository;
    private readonly ILogger<FavoriteSymbolController> _logger;

    public FavoriteSymbolController(IFavoriteSymbolRepository repository, ILogger<FavoriteSymbolController> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> GetFavorites([FromQuery] int userId = 1)
    {
        try
        {
            return Ok(await _repository.GetFavoritesAsync(NormalizeUser(userId)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load favorite symbols for user {UserId}.", userId);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPut("{symbol}")]
    public async Task<IActionResult> AddFavorite(string symbol, [FromQuery] int userId = 1)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol is required.");
        try
        {
            await _repository.AddFavoriteAsync(NormalizeUser(userId), symbol);
            return Ok(new { success = true, symbol = symbol.Trim().ToUpperInvariant(), isFavorite = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add favorite {Symbol} for user {UserId}.", symbol, userId);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpDelete("{symbol}")]
    public async Task<IActionResult> RemoveFavorite(string symbol, [FromQuery] int userId = 1)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("Symbol is required.");
        try
        {
            await _repository.RemoveFavoriteAsync(NormalizeUser(userId), symbol);
            return Ok(new { success = true, symbol = symbol.Trim().ToUpperInvariant(), isFavorite = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove favorite {Symbol} for user {UserId}.", symbol, userId);
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    private static int NormalizeUser(int userId) => userId > 0 ? userId : 1;
}
