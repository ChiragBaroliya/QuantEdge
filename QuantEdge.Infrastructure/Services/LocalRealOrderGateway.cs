using System;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Interfaces;

namespace QuantEdge.Infrastructure.Services;

/// <summary>
/// Places the order from this process. Used inside QuantEdge.Worker (whose server IP is registered with
/// Zerodha) and by the API when it runs on that same server (RealOrderExecution:Mode = Local).
/// </summary>
public class LocalRealOrderGateway : IRealOrderGateway
{
    private readonly IAutoRealTradeService _realTradeService;

    public LocalRealOrderGateway(IAutoRealTradeService realTradeService)
    {
        _realTradeService = realTradeService ?? throw new ArgumentNullException(nameof(realTradeService));
    }

    public async Task<RealOrderExecutionResultDto> ManualBuyAsync(ManualRealBuyRequestDto dto)
    {
        bool executed = await _realTradeService.EvaluateAndExecuteRealBuyAsync(
            dto.Symbol, dto.EntryPrice, dto.MetConditionsCount, UserIdOf(dto.UserId), isBuySignal: true,
            isManualTrade: true, manualQuantity: dto.Quantity, manualStopLossPct: dto.StopLossPct, manualTrailingSlPct: dto.TrailingSlPct);

        return new RealOrderExecutionResultDto { Success = executed };
    }

    public async Task<RealOrderExecutionResultDto> ManualSellAsync(ManualSellRequestDto dto)
    {
        var (success, message) = await _realTradeService.ManualSellAsync(
            dto.Symbol, dto.Quantity, dto.CurrentPrice, dto.Product, dto.EntryPriceHint, dto.Reason ?? "Manual Web Sell", UserIdOf(dto.UserId));

        return new RealOrderExecutionResultDto { Success = success, Message = message };
    }

    public async Task<RealOrderExecutionResultDto> ManualBuyBackAsync(ManualSellRequestDto dto)
    {
        var (success, message) = await _realTradeService.ManualBuyBackAsync(
            dto.Symbol, dto.Quantity, dto.CurrentPrice, dto.Product, dto.EntryPriceHint, dto.Reason ?? "Manual Web Buy Back", UserIdOf(dto.UserId));

        return new RealOrderExecutionResultDto { Success = success, Message = message };
    }

    public async Task<RealOrderExecutionResultDto> SquareOffPositionAsync(CloseRealPositionRequestDto dto)
    {
        bool success = await _realTradeService.SquareOffSinglePositionAsync(dto.PositionId, dto.Reason ?? "Manual Web Square-Off", UserIdOf(dto.UserId));
        return new RealOrderExecutionResultDto
        {
            Success = success,
            Message = success ? "Position square-off initiated." : "Failed to square off position."
        };
    }

    public async Task<RealOrderExecutionResultDto> SquareOffAllAsync(EmergencyKillSwitchRequestDto dto)
    {
        int closedCount = await _realTradeService.SquareOffAllPositionsAsync(dto.Reason ?? "Emergency Panic Kill Switch Triggered by User", UserIdOf(dto.UserId));
        return new RealOrderExecutionResultDto
        {
            Success = true,
            ClosedPositionsCount = closedCount,
            Message = $"Kill switch activated. {closedCount} positions squared off."
        };
    }

    private static int UserIdOf(int? userId) => userId.HasValue && userId.Value > 0 ? userId.Value : 1;
}
