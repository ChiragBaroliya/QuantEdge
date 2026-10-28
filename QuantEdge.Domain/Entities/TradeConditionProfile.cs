using System;
using System.Collections.Generic;

namespace QuantEdge.Domain.Entities;

/// <summary>
/// Which of the 11 BUY / SHORT conditions one user's bot applies, for one trading mode and side
/// (trade_condition_profiles). No row = all 11 on with the mode settings' MinConditionsMatch - exactly the
/// behaviour from before this feature existed.
/// </summary>
public class TradeConditionProfile
{
    public const string ModeAutoPaper = "AUTO_PAPER";
    public const string ModeAutoReal = "AUTO_REAL";
    public const string SideBuy = "BUY";
    public const string SideShort = "SHORT";

    public static readonly string[] Modes = { ModeAutoPaper, ModeAutoReal };
    public static readonly string[] Sides = { SideBuy, SideShort };

    /// <summary>The mode's own user key: AutoTradeSettings.UserId for Auto Paper, the RealTradeSettings user id for Auto Real.</summary>
    public string UserRef { get; set; } = string.Empty;
    public string TradeMode { get; set; } = ModeAutoPaper;
    public string Side { get; set; } = SideBuy;

    /// <summary>Keys (TradeConditionRules) of the conditions switched OFF. Empty = all 11 on.</summary>
    public List<string> DisabledConditions { get; set; } = new();

    /// <summary>Min conditions to match, counted over the conditions that are ON. Null = the mode settings' MinConditionsMatch.</summary>
    public int? MinConditionsMatch { get; set; }

    /// <summary>When the user confirmed the risk of turning a hard filter off on Auto Real.</summary>
    public DateTime? RiskAcceptedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsShort => string.Equals(Side, SideShort, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this profile changes nothing - the bot then runs its original code path.</summary>
    public bool IsDefault => DisabledConditions.Count == 0 && !MinConditionsMatch.HasValue;

    public bool IsEnabled(string conditionKey) => !DisabledConditions.Contains(conditionKey);

    public static TradeConditionProfile Default(string userRef, string tradeMode, string side) =>
        new() { UserRef = userRef, TradeMode = tradeMode, Side = side };
}
