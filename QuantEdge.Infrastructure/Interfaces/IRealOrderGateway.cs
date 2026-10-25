using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

/// <summary>
/// Entry point for every user-triggered Real Trade action that places an order at Zerodha (Manual Buy,
/// Manual Sell, Buy Back, Square-Off, Kill Switch). Zerodha only accepts order placement from the static
/// IPs registered on the Kite app, and those belong to the QuantEdge.Worker server - so when the API is
/// deployed on a different server, the Remote implementation forwards these calls to the Worker's internal
/// endpoint instead of calling Zerodha from the API's own (unregistered) IP.
/// Every DTO passed in must already carry the resolved UserId (and Reason, where applicable).
/// </summary>
public interface IRealOrderGateway
{
    Task<RealOrderExecutionResultDto> ManualBuyAsync(ManualRealBuyRequestDto dto);

    Task<RealOrderExecutionResultDto> ManualSellAsync(ManualSellRequestDto dto);

    Task<RealOrderExecutionResultDto> ManualBuyBackAsync(ManualSellRequestDto dto);

    Task<RealOrderExecutionResultDto> SquareOffPositionAsync(CloseRealPositionRequestDto dto);

    Task<RealOrderExecutionResultDto> SquareOffAllAsync(EmergencyKillSwitchRequestDto dto);
}
