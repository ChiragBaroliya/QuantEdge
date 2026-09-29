using System;
using System.Collections.Generic;

namespace QuantEdge.Infrastructure.DTOs;

/// <summary>
/// One entry in the header notification bell. Id is stable across refreshes so the browser can
/// track what it has already seen. Category: real-trade | swing | nifty. Level: success | info | warning | error.
/// </summary>
public record NotificationItemDto(
    string Id,
    string Category,
    string Level,
    string Title,
    string Message,
    string? Symbol,
    DateTime TimeUtc
);

/// <summary>Today's (IST calendar day) notifications, newest first.</summary>
public record TodayNotificationsDto(
    DateTime DateIst,
    List<NotificationItemDto> Items
);
