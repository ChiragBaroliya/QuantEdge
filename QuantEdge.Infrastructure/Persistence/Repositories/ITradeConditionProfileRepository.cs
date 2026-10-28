using System.Collections.Generic;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;

namespace QuantEdge.Infrastructure.Persistence.Repositories;

public interface ITradeConditionProfileRepository
{
    /// <summary>The BUY and SHORT profiles of one user and mode - a side with no stored row comes back as the default.</summary>
    Task<IReadOnlyList<TradeConditionProfile>> GetModeAsync(string userRef, string tradeMode);

    /// <summary>Every stored profile of one mode, for the scan workers (users without rows use the default).</summary>
    Task<IReadOnlyList<TradeConditionProfile>> GetAllForModeAsync(string tradeMode);

    Task SaveAsync(TradeConditionProfile profile);

    /// <summary>Deletes the user's profiles for the mode - back to the default (all 11 on).</summary>
    Task DeleteModeAsync(string userRef, string tradeMode);
}
