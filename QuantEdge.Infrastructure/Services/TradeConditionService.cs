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
/// Trade Conditions page backend. Rules enforced on save (both sides of a mode at once):
///   - only known condition keys;
///   - at least one scoring condition (4-11) ON, so there is a score;
///   - min conditions to match kept within 1..conditions ON;
///   - Auto Real with a hard filter OFF needs the user's risk confirmation.
/// A side left at the default (all on, the mode setting's min) is stored with no min, so the bot keeps its original path.
/// </summary>
public class TradeConditionService : ITradeConditionService
{
    private readonly ITradeConditionProfileRepository _repository;
    private readonly ISwingStrategySettingsRepository _strategySettingsRepository;
    private readonly IAutoTradeService _autoTradeService;
    private readonly IAutoRealTradeService _realTradeService;
    private readonly ILogger<TradeConditionService> _logger;

    public TradeConditionService(
        ITradeConditionProfileRepository repository,
        ISwingStrategySettingsRepository strategySettingsRepository,
        IAutoTradeService autoTradeService,
        IAutoRealTradeService realTradeService,
        ILogger<TradeConditionService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _strategySettingsRepository = strategySettingsRepository ?? throw new ArgumentNullException(nameof(strategySettingsRepository));
        _autoTradeService = autoTradeService ?? throw new ArgumentNullException(nameof(autoTradeService));
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TradeConditionModeDto> GetModeAsync(string tradeMode, string userRef)
    {
        tradeMode = NormalizeMode(tradeMode);
        var strategy = await _strategySettingsRepository.GetSettingsAsync();
        int settingsMin = await GetSettingsMinMatchAsync(tradeMode, userRef);
        var profiles = await _repository.GetModeAsync(userRef, tradeMode);
        return ToDto(tradeMode, userRef, strategy, settingsMin, profiles);
    }

    public async Task<(bool Success, string? Error, TradeConditionModeDto? Mode)> SaveModeAsync(string tradeMode, string userRef, TradeConditionSaveDto dto)
    {
        tradeMode = NormalizeMode(tradeMode);
        if (dto?.Buy == null || dto.Short == null)
            return (false, "Both BUY and SHORT selections are required.", null);

        var strategy = await _strategySettingsRepository.GetSettingsAsync();
        bool regimeMode = strategy.UsesRegimeGate;
        int settingsMin = await GetSettingsMinMatchAsync(tradeMode, userRef);
        var existing = await _repository.GetModeAsync(userRef, tradeMode);

        var toSave = new List<TradeConditionProfile>();
        foreach (var (side, sideDto) in new[] { (TradeConditionProfile.SideBuy, dto.Buy), (TradeConditionProfile.SideShort, dto.Short) })
        {
            var disabled = (sideDto.DisabledConditions ?? new List<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim().ToUpperInvariant())
                .Distinct()
                .ToList();

            var unknown = disabled.Where(k => !TradeConditionRules.IsKnownKey(k)).ToList();
            if (unknown.Count > 0)
                return (false, $"{side}: unknown condition(s) {string.Join(", ", unknown)}.", null);

            var profile = new TradeConditionProfile
            {
                UserRef = userRef,
                TradeMode = tradeMode,
                Side = side,
                DisabledConditions = TradeConditionRules.All.Where(c => disabled.Contains(c.Key)).Select(c => c.Key).ToList()
            };

            if (TradeConditionRules.EnabledMaxPoints(profile, regimeMode) == 0)
                return (false, $"{side}: turn on at least one scoring condition (4-11) - without one there is no score.", null);

            int enabled = TradeConditionRules.EnabledCount(profile, regimeMode);
            int min = Math.Clamp(sideDto.MinConditionsMatch, 1, Math.Max(1, enabled));
            // Nothing changed from the default: store no min, so the side keeps following the mode setting.
            profile.MinConditionsMatch = profile.DisabledConditions.Count == 0 && min == settingsMin ? null : min;

            bool hardFilterOff = TradeConditionRules.All.Any(c => c.IsHardFilter && !TradeConditionRules.IsApplied(profile, c, regimeMode)
                && !TradeConditionRules.IsManagedByRegime(c, profile.IsShort, regimeMode));
            if (hardFilterOff && tradeMode == TradeConditionProfile.ModeAutoReal)
            {
                if (!dto.RiskAccepted)
                    return (false, $"{side}: a hard filter is OFF on Auto Real - confirm the risk to save.", null);
                profile.RiskAcceptedAt = existing.FirstOrDefault(p => p.Side == side)?.RiskAcceptedAt ?? DateTime.UtcNow;
            }

            toSave.Add(profile);
        }

        foreach (var profile in toSave)
        {
            await _repository.SaveAsync(profile);
            _logger.LogInformation("Trade Conditions saved for {Mode} {Side} (user {UserRef}): OFF [{Disabled}], min {Min}.",
                tradeMode, profile.Side, userRef, string.Join(", ", profile.DisabledConditions), profile.MinConditionsMatch?.ToString() ?? "mode setting");
        }

        return (true, null, ToDto(tradeMode, userRef, strategy, settingsMin, toSave));
    }

    public async Task<TradeConditionModeDto> ResetModeAsync(string tradeMode, string userRef)
    {
        tradeMode = NormalizeMode(tradeMode);
        await _repository.DeleteModeAsync(userRef, tradeMode);
        _logger.LogInformation("Trade Conditions reset to default for {Mode} (user {UserRef}).", tradeMode, userRef);
        return await GetModeAsync(tradeMode, userRef);
    }

    private static string NormalizeMode(string? tradeMode)
    {
        string mode = (tradeMode ?? string.Empty).Trim().ToUpperInvariant();
        return TradeConditionProfile.Modes.Contains(mode)
            ? mode
            : throw new ArgumentException($"Unknown trade mode '{tradeMode}' (expected AUTO_PAPER or AUTO_REAL).", nameof(tradeMode));
    }

    private async Task<int> GetSettingsMinMatchAsync(string tradeMode, string userRef)
    {
        if (tradeMode == TradeConditionProfile.ModeAutoReal)
        {
            int userId = int.TryParse(userRef, out var uid) ? uid : 1;
            return (await _realTradeService.GetSettingsAsync(userId)).MinConditionsMatch;
        }
        return (await _autoTradeService.GetSettingsAsync(userRef)).MinConditionsMatch;
    }

    private static TradeConditionModeDto ToDto(string tradeMode, string userRef, SwingStrategySettings strategy, int settingsMin,
        IReadOnlyList<TradeConditionProfile> profiles)
    {
        bool regimeMode = strategy.UsesRegimeGate;
        TradeConditionSideDto Side(string side)
        {
            var p = profiles.FirstOrDefault(x => x.Side == side) ?? TradeConditionProfile.Default(userRef, tradeMode, side);
            var hardFilters = TradeConditionRules.All.Where(c => c.IsHardFilter && !TradeConditionRules.IsManagedByRegime(c, p.IsShort, regimeMode)).ToList();
            return new TradeConditionSideDto
            {
                Side = side,
                DisabledConditions = p.DisabledConditions.ToList(),
                MinConditionsMatch = TradeConditionRules.EffectiveMinMatch(p, regimeMode, settingsMin),
                EnabledCount = TradeConditionRules.EnabledCount(p, regimeMode),
                TotalCount = TradeConditionRules.ConfigurableCount(p.IsShort, regimeMode),
                HardFiltersOn = hardFilters.Count(c => p.IsEnabled(c.Key)),
                HardFiltersTotal = hardFilters.Count,
                EnabledMaxPoints = TradeConditionRules.EnabledMaxPoints(p, regimeMode),
                IsDefault = p.IsDefault,
                RiskAcceptedAt = p.RiskAcceptedAt,
                UpdatedAt = p.IsDefault ? null : p.UpdatedAt
            };
        }

        return new TradeConditionModeDto
        {
            TradeMode = tradeMode,
            UserRef = userRef,
            GateMode = strategy.MarketGateMode ?? SwingStrategySettings.GateModeNiftyFilter,
            BuyScoreThreshold = strategy.BuyScoreThreshold,
            WatchScoreThreshold = strategy.WatchScoreThreshold,
            SettingsMinConditionsMatch = settingsMin,
            Buy = Side(TradeConditionProfile.SideBuy),
            Short = Side(TradeConditionProfile.SideShort)
        };
    }
}
