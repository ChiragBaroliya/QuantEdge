using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Web.Controllers;

/// <summary>
/// Admin-only Trade Flow screen: flow diagrams explaining how the Auto Real Trade background jobs work.
/// The rules snapshot is fetched server-side, so the browser never calls the API for it.
/// </summary>
[Authorize(Roles = "Admin")]
public class TradeLogicController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TradeLogicController> _logger;

    public TradeLogicController(IHttpClientFactory httpClientFactory, ILogger<TradeLogicController> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        int userId = int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : 1;

        TradeLogicRulesDto? rules = null;
        try
        {
            var client = _httpClientFactory.CreateClient("QuantEdgeApi");
            rules = await client.GetFromJsonAsync<TradeLogicRulesDto>($"/api/tradelogic/rules?userId={userId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load trade logic rules from API; showing defaults.");
        }

        rules ??= TradeLogicRulesDto.Create(new RealTradeSettings { UserId = userId }, SwingStrategySettings.Default, isLive: false);
        return View(rules);
    }
}
