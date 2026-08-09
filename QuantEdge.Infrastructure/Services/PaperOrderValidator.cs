using System;
using System.Threading.Tasks;
using QuantEdge.Domain.Entities;
using QuantEdge.Domain.Exceptions;
using QuantEdge.Infrastructure.DTOs;
using QuantEdge.Infrastructure.Persistence.Repositories;

namespace QuantEdge.Infrastructure.Services;

public class PaperOrderValidator
{
    private readonly IStockMasterRepository _stockRepository;

    public PaperOrderValidator(IStockMasterRepository stockRepository)
    {
        _stockRepository = stockRepository ?? throw new ArgumentNullException(nameof(stockRepository));
    }

    public async Task ValidateOrderPlacementAsync(PlacePaperOrderDto dto, PaperAccount account, decimal currentLtp)
    {
        if (dto == null)
            throw new InvalidOrderException("Order request payload cannot be empty.", "INVALID_PAYLOAD");

        if (string.IsNullOrWhiteSpace(dto.Symbol))
            throw new InvalidOrderException("Please select a valid stock or index symbol.", "INVALID_SYMBOL");

        // 1. Symbol Validation
        var stock = await _stockRepository.GetBySymbolAsync(dto.Symbol);
        if (stock == null)
        {
            throw new InvalidOrderException($"Symbol '{dto.Symbol}' is not supported for trading.", "SYMBOL_NOT_FOUND");
        }

        // 2. Quantity Validation
        if (dto.Quantity <= 0)
        {
            throw new InvalidOrderException("Order quantity must be a positive number greater than 0.", "INVALID_QUANTITY");
        }

        // 3. Execution Price Determination
        decimal executionPrice = dto.OrderType == PaperOrderType.Market ? currentLtp : dto.Price;
        if (executionPrice <= 0m)
        {
            if (dto.OrderType == PaperOrderType.Market)
            {
                throw new InvalidOrderException($"Live market price for '{dto.Symbol}' is currently unavailable. Please try again in a moment.", "MARKET_PRICE_UNAVAILABLE");
            }
            else
            {
                throw new InvalidOrderException("Limit price must be greater than zero.", "INVALID_LIMIT_PRICE");
            }
        }

        // 4. Stop-Loss & Take-Profit Logic Validation
        if (dto.StopLoss.HasValue)
        {
            if (dto.StopLoss.Value <= 0m)
            {
                throw new InvalidOrderException("Stop-loss price must be greater than zero.", "INVALID_STOP_LOSS");
            }

            if (dto.Side == TradeSide.BUY && dto.StopLoss.Value >= executionPrice)
            {
                throw new InvalidOrderException($"For a BUY order, your Stop-Loss (₹{dto.StopLoss.Value:N2}) must be lower than the entry price (₹{executionPrice:N2}).", "INVALID_SL_BUY");
            }
            if (dto.Side == TradeSide.SELL && dto.StopLoss.Value <= executionPrice)
            {
                throw new InvalidOrderException($"For a SELL order, your Stop-Loss (₹{dto.StopLoss.Value:N2}) must be higher than the entry price (₹{executionPrice:N2}).", "INVALID_SL_SELL");
            }
        }

        if (dto.TakeProfit.HasValue)
        {
            if (dto.TakeProfit.Value <= 0m)
            {
                throw new InvalidOrderException("Target profit price must be greater than zero.", "INVALID_TAKE_PROFIT");
            }

            if (dto.Side == TradeSide.BUY && dto.TakeProfit.Value <= executionPrice)
            {
                throw new InvalidOrderException($"For a BUY order, your Target Profit (₹{dto.TakeProfit.Value:N2}) must be higher than the entry price (₹{executionPrice:N2}).", "INVALID_TP_BUY");
            }
            if (dto.Side == TradeSide.SELL && dto.TakeProfit.Value >= executionPrice)
            {
                throw new InvalidOrderException($"For a SELL order, your Target Profit (₹{dto.TakeProfit.Value:N2}) must be lower than the entry price (₹{executionPrice:N2}).", "INVALID_TP_SELL");
            }
        }

        // 5. Margin Sufficiency Validation
        decimal requiredMargin = dto.Quantity * executionPrice;
        if (account.AvailableMargin < requiredMargin)
        {
            throw new InsufficientFundsException(requiredMargin, account.AvailableMargin);
        }
    }
}
