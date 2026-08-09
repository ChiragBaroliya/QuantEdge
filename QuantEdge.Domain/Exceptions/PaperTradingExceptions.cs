using System;

namespace QuantEdge.Domain.Exceptions;

public class PaperTradingException : Exception
{
    public string ErrorCode { get; }

    public PaperTradingException(string message, string errorCode = "PAPER_TRADING_ERROR") 
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

public class InsufficientFundsException : PaperTradingException
{
    public decimal RequiredMargin { get; }
    public decimal AvailableMargin { get; }

    public InsufficientFundsException(decimal requiredMargin, decimal availableMargin) 
        : base($"Insufficient Margin Funds: You need ₹{requiredMargin:N2} margin for this trade, but only ₹{availableMargin:N2} is available. Try reducing quantity or closing an existing position.", "INSUFFICIENT_FUNDS")
    {
        RequiredMargin = requiredMargin;
        AvailableMargin = availableMargin;
    }
}

public class InvalidOrderException : PaperTradingException
{
    public InvalidOrderException(string message, string errorCode = "INVALID_ORDER") 
        : base(message, errorCode)
    {
    }
}

public class PositionNotFoundException : PaperTradingException
{
    public PositionNotFoundException(int positionId) 
        : base($"Position with ID {positionId} was not found or is already closed.", "POSITION_NOT_FOUND")
    {
    }
}

public class OrderNotFoundException : PaperTradingException
{
    public OrderNotFoundException(int orderId) 
        : base($"Order with ID {orderId} was not found or has already been processed.", "ORDER_NOT_FOUND")
    {
    }
}
