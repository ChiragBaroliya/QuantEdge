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

/// <summary>
/// Notifications for an IST date range (View All Notifications page), newest first. Truncated = a source hit its
/// row limit, so older items in the range may be missing - narrow the range to see them.
/// </summary>
public record NotificationHistoryDto(
    DateTime FromIst,
    DateTime ToIst,
    List<NotificationItemDto> Items,
    bool Truncated
);
