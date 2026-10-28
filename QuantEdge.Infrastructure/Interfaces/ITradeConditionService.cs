using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>Trade Conditions page: load, validate and save which of the 11 conditions a user's bot applies.</summary>
public interface ITradeConditionService
{
    Task<TradeConditionModeDto> GetModeAsync(string tradeMode, string userRef);

    /// <summary>Validates and saves both sides of one mode. Returns the saved state, or the reason it was rejected.</summary>
    Task<(bool Success, string? Error, TradeConditionModeDto? Mode)> SaveModeAsync(string tradeMode, string userRef, TradeConditionSaveDto dto);

    /// <summary>Back to the default for the mode: all 11 on, the mode settings' min conditions.</summary>
    Task<TradeConditionModeDto> ResetModeAsync(string tradeMode, string userRef);
}
