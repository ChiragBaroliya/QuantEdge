using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Trade Conditions page > Rule numbers backend: the numbers behind the 11 conditions, the score thresholds, the market
/// gate mode and the regime policies. Every value is checked before anything is saved, a save must confirm that it also
/// drives the real-money bot, and each changed value is logged with its old and new value.
/// </summary>
public class StrategyRulesService : IStrategyRulesService
{
    private const int MaxPolicyPositions = 50;
    private const decimal MinPolicyRiskPct = 0.05m;
    private const decimal MaxPolicyRiskPct = 5m;

    private readonly ISwingStrategySettingsRepository _settingsRepository;
    private readonly IMarketRegimeService _regimeService;
    private readonly ILogger<StrategyRulesService> _logger;

    public StrategyRulesService(ISwingStrategySettingsRepository settingsRepository, IMarketRegimeService regimeService, ILogger<StrategyRulesService> logger)
    {
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _regimeService = regimeService ?? throw new ArgumentNullException(nameof(regimeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StrategyRulesDto> GetAsync()
    {
        var settings = await _settingsRepository.GetSettingsAsync();
        return ToDto(settings, await _regimeService.GetPoliciesAsync());
    }

    public async Task<(bool Success, IReadOnlyList<string> Errors, StrategyRulesDto? Saved)> SaveAsync(StrategyRulesSaveDto dto)
    {
        var errors = Validate(dto, out string gateMode);
        if (errors.Count > 0) return (false, errors, null);

        var before = await _settingsRepository.GetSettingsAsync();
        var beforePolicies = await _regimeService.GetPoliciesAsync();
        var changes = DescribeChanges(before, beforePolicies, dto, gateMode);

        var policies = dto.Policies!.Select(p =>
        {
            var existing = beforePolicies.FirstOrDefault(x => x.Regime == p.Regime);
            var fallback = RegimePolicy.Defaults.First(x => x.Regime == p.Regime);
            return new RegimePolicy
            {
                Regime = p.Regime,
                MinStockScore = p.MinStockScore,
                RequireRelativeStrength = p.RequireRelativeStrength,
                MaxPositions = p.MaxPositions,
                RiskPct = p.RiskPct,
                MaxExposurePct = existing?.MaxExposurePct ?? fallback.MaxExposurePct,
                Description = existing?.Description ?? fallback.Description
            };
        }).ToList();

        await _regimeService.SavePoliciesAsync(policies);
        var saved = await _settingsRepository.UpdateRulesAsync(dto.Rules!, dto.BuyScoreThreshold, dto.WatchScoreThreshold, gateMode);

        _logger.LogWarning("Strategy rule numbers saved ({Count} changed): {Changes}", changes.Count, changes.Count == 0 ? "nothing" : string.Join("; ", changes));
        return (true, Array.Empty<string>(), ToDto(saved, await _regimeService.GetPoliciesAsync()));
    }

    private static List<string> Validate(StrategyRulesSaveDto dto, out string gateMode)
    {
        gateMode = (dto?.MarketGateMode ?? string.Empty).Trim().ToUpperInvariant();
        if (dto == null) return new List<string> { "Nothing to save." };

        var errors = new List<string>();
        if (!dto.ConfirmLive) errors.Add("Confirm that these numbers also apply to the real-money Auto Real bot.");

        errors.AddRange(SwingRuleCatalog.Validate(dto.Rules));

        if (dto.BuyScoreThreshold is < 0 or > 100 || dto.WatchScoreThreshold is < 0 or > 100) errors.Add("Score thresholds must be between 0 and 100.");
        if (dto.BuyScoreThreshold <= dto.WatchScoreThreshold) errors.Add("The BUY score threshold must be greater than the WATCH threshold.");
        if (gateMode != SwingStrategySettings.GateModeNiftyFilter && gateMode != SwingStrategySettings.GateModeRegime)
            errors.Add($"Market gate must be {SwingStrategySettings.GateModeNiftyFilter} or {SwingStrategySettings.GateModeRegime}.");

        var known = RegimePolicy.Defaults.Select(p => p.Regime).ToList();
        var given = dto.Policies ?? new List<RegimePolicy>();
        if (given.Select(p => p.Regime).Distinct().Count() != given.Count || !known.All(r => given.Any(p => p.Regime == r)) || given.Count != known.Count)
        {
            errors.Add($"Send exactly one policy for each regime: {string.Join(", ", known)}.");
        }
        foreach (var p in given)
        {
            if (p.MinStockScore is < 0 or > 100) errors.Add($"{p.Regime}: minimum score must be between 0 and 100.");
            if (p.MaxPositions is < 0 or > MaxPolicyPositions) errors.Add($"{p.Regime}: max positions must be between 0 and {MaxPolicyPositions}.");
            if (p.RiskPct < MinPolicyRiskPct || p.RiskPct > MaxPolicyRiskPct) errors.Add($"{p.Regime}: risk must be between {MinPolicyRiskPct}% and {MaxPolicyRiskPct}%.");
        }
        return errors;
    }

    private static List<string> DescribeChanges(SwingStrategySettings before, IReadOnlyList<RegimePolicy> beforePolicies, StrategyRulesSaveDto dto, string gateMode)
    {
        var changes = new List<string>();
        foreach (var f in SwingRuleCatalog.Fields)
        {
            decimal from = f.Read(before.Rules), to = f.Read(dto.Rules!);
            if (from != to) changes.Add($"{f.Key} {from:0.##} -> {to:0.##}");
        }
        if (before.BuyScoreThreshold != dto.BuyScoreThreshold) changes.Add($"buyScoreThreshold {before.BuyScoreThreshold} -> {dto.BuyScoreThreshold}");
        if (before.WatchScoreThreshold != dto.WatchScoreThreshold) changes.Add($"watchScoreThreshold {before.WatchScoreThreshold} -> {dto.WatchScoreThreshold}");
        if (!string.Equals(before.MarketGateMode, gateMode, StringComparison.OrdinalIgnoreCase)) changes.Add($"marketGateMode {before.MarketGateMode} -> {gateMode}");
        foreach (var p in dto.Policies!)
        {
            var old = beforePolicies.FirstOrDefault(x => x.Regime == p.Regime);
            if (old == null || old.MinStockScore != p.MinStockScore || old.RequireRelativeStrength != p.RequireRelativeStrength || old.MaxPositions != p.MaxPositions || old.RiskPct != p.RiskPct)
            {
                changes.Add($"{p.Regime} policy {old?.MinStockScore}/{old?.RequireRelativeStrength}/{old?.MaxPositions}/{old?.RiskPct:0.##} -> {p.MinStockScore}/{p.RequireRelativeStrength}/{p.MaxPositions}/{p.RiskPct:0.##}");
            }
        }
        return changes;
    }

    private static StrategyRulesDto ToDto(SwingStrategySettings settings, IReadOnlyList<RegimePolicy> policies) => new()
    {
        Rules = settings.Rules,
        Defaults = new SwingRuleParameters(),
        Fields = SwingRuleCatalog.Fields,
        Policies = RegimePolicy.Defaults.Select(d => policies.FirstOrDefault(p => p.Regime == d.Regime) ?? d).ToList(),
        DefaultPolicies = RegimePolicy.Defaults,
        BuyScoreThreshold = settings.BuyScoreThreshold,
        WatchScoreThreshold = settings.WatchScoreThreshold,
        MarketGateMode = settings.MarketGateMode ?? SwingStrategySettings.GateModeNiftyFilter,
        UpdatedAt = settings.UpdatedAt
    };
}
