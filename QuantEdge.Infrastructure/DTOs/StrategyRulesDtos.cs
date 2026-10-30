using System;
using System.Collections.Generic;
using QuantEdge.Domain.Entities;
using QuantEdge.Infrastructure.Services;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>Everything the Rule numbers page shows: the saved values, the original ones and how to draw each field.</summary>
public class StrategyRulesDto
{
    public SwingRuleParameters Rules { get; set; } = new();
    public SwingRuleParameters Defaults { get; set; } = new();
    public IReadOnlyList<SwingRuleField> Fields { get; set; } = Array.Empty<SwingRuleField>();
    public IReadOnlyList<RegimePolicy> Policies { get; set; } = Array.Empty<RegimePolicy>();
    public IReadOnlyList<RegimePolicy> DefaultPolicies { get; set; } = Array.Empty<RegimePolicy>();
    public int RequiredTotalPoints { get; set; } = SwingRuleCatalog.RequiredTotalPoints;
    public int BuyScoreThreshold { get; set; }
    public int WatchScoreThreshold { get; set; }
    public string MarketGateMode { get; set; } = SwingStrategySettings.GateModeNiftyFilter;
    public DateTime UpdatedAt { get; set; }
}

public class StrategyRulesSaveDto
{
    public SwingRuleParameters? Rules { get; set; }
    public List<RegimePolicy>? Policies { get; set; }
    public int BuyScoreThreshold { get; set; }
    public int WatchScoreThreshold { get; set; }
    public string? MarketGateMode { get; set; }
    /// <summary>The saver confirmed these numbers also drive the real-money bot.</summary>
    public bool ConfirmLive { get; set; }
}
