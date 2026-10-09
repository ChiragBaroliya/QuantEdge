using System.Threading;
using System.Threading.Tasks;
using QuantEdge.Infrastructure.DTOs;

namespace QuantEdge.Infrastructure.Interfaces;

public interface INotificationService
{
    /// <summary>
    /// Today's (IST) notifications for the header bell, built from data that already exists:
    /// real trade execution logs, swing scan slots with BUY picks, and a NIFTY market filter flip.
    /// Nothing from a previous day is ever returned.
    /// </summary>
    Task<TodayNotificationsDto> GetTodayAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every notification from <paramref name="fromDateIst"/> to <paramref name="toDateIst"/> (IST calendar days,
    /// inclusive, at most 31 days, never in the future) - same sources as the bell. For the View All Notifications page.
    /// </summary>
    Task<NotificationHistoryDto> GetHistoryAsync(int userId, System.DateTime fromDateIst, System.DateTime toDateIst,
        CancellationToken cancellationToken = default);
}
