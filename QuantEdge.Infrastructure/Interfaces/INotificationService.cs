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
}
