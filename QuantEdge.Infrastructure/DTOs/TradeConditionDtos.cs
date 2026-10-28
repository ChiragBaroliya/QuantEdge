using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>One side (BUY / SHORT) of a user's Trade Conditions profile, with the counts the pages show.</summary>
public class TradeConditionSideDto
{
    public string Side { get; set; } = "BUY";
    public List<string> DisabledConditions { get; set; } = new();
    /// <summary>Effective min conditions to match (the profile's, else the mode setting), within 1..EnabledCount.</summary>
    public int MinConditionsMatch { get; set; }
    public int EnabledCount { get; set; }
    /// <summary>Conditions this page decides: 11, or 10 for BUY in REGIME gate mode (condition 1 is the regime's).</summary>
    public int TotalCount { get; set; }
    public int HardFiltersOn { get; set; }
    public int HardFiltersTotal { get; set; }
    public int EnabledMaxPoints { get; set; }
    /// <summary>True when nothing was changed from the default (all on, mode setting's min).</summary>
    public bool IsDefault { get; set; }
    public DateTime? RiskAcceptedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class TradeConditionModeDto
{
    public string TradeMode { get; set; } = "AUTO_PAPER";
    public string UserRef { get; set; } = string.Empty;
    public string GateMode { get; set; } = "NIFTY_FILTER";
    public int BuyScoreThreshold { get; set; }
    public int WatchScoreThreshold { get; set; }
    /// <summary>MinConditionsMatch from the mode's own settings - used for a side never saved on this page.</summary>
    public int SettingsMinConditionsMatch { get; set; }
    public TradeConditionSideDto Buy { get; set; } = new();
    public TradeConditionSideDto Short { get; set; } = new() { Side = "SHORT" };
}

public class TradeConditionSideSaveDto
{
    public List<string> DisabledConditions { get; set; } = new();
    public int MinConditionsMatch { get; set; }
}

public class TradeConditionSaveDto
{
    public TradeConditionSideSaveDto Buy { get; set; } = new();
    public TradeConditionSideSaveDto Short { get; set; } = new();
    /// <summary>Auto Real only: required when any hard filter is OFF.</summary>
    public bool RiskAccepted { get; set; }
}
