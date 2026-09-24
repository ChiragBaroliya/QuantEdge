using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.API.Controllers;

/// <summary>
/// Read-only rules snapshot for the admin Trade Flow screen (QuantEdge.Web TradeLogicController).
/// Exposes settings and rule constants only - no positions, orders or credentials.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TradeLogicController : ControllerBase
{
    private readonly IAutoRealTradeService _realTradeService;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;

    public TradeLogicController(IAutoRealTradeService realTradeService, ISwingStrategySettingsRepository strategySettingsRepository)
    {
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] int userId = 1)
    {
        var settings = await _realTradeService.GetSettingsAsync(userId) ?? new RealTradeSettings { UserId = userId };
        var strategy = await _strategySettingsRepository.GetSettingsAsync() ?? SwingStrategySettings.Default;
        return Ok(TradeLogicRulesDto.Create(settings, strategy));
    }
}
