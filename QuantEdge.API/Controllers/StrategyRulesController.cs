using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.API.Controllers;

/// <summary>Trade Conditions page > Rule numbers: every number behind the 11 conditions, the thresholds, the gate mode and the regime policies.</summary>
[ApiController]
[Route("api/[controller]")]
public class StrategyRulesController : ControllerBase
{
    private readonly IStrategyRulesService _service;

    public StrategyRulesController(IStrategyRulesService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await _service.GetAsync());

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] StrategyRulesSaveDto dto)
    {
        var (success, errors, saved) = await _service.SaveAsync(dto);
        return success ? Ok(saved) : BadRequest(new { message = string.Join(" ", errors), errors });
    }
}
