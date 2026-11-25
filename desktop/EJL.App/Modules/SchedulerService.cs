using EJL.Data;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

/// <summary>Фоновый планировщик: первая проверка через 5 секунд после старта, далее раз в минуту.</summary>
public sealed class SchedulerService(
    IDbContextFactory<EjlDbContext> dbf, ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var created = await NotificationsModule.RunChecks(dbf);
                if (created > 0)
                    logger.LogInformation("Планировщик: создано уведомлений — {Created}", created);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка цикла проверок планировщика");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
