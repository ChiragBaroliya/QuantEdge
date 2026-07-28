namespace QuantEdge.Domain.Entities;

public enum PaperOrderStatus
{
    Pending = 0,
    Filled = 1,
    Cancelled = 2,
    Rejected = 3
}

public enum PaperOrderType
{
    Market = 0,
    Limit = 1,
    StopLoss = 2
}

public enum TradeSide
{
    BUY = 0,
    SELL = 1
}

public enum PositionStatus
{
    OPEN = 0,
    CLOSED = 1
}
