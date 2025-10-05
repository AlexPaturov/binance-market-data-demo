using Hangfire.Annotations;
using Hangfire.Dashboard;

namespace BinanceDataCollector.DataManager.Common;

public class AllowAllConnectionsFilter : IDashboardAuthorizationFilter
{
    public bool Authorize([NotNull] DashboardContext context)
    {
        // Разрешаем все подключения к дашборду.
        // ВАЖНО: В реальном продакшене здесь должна быть проверка
        // на аутентификацию и права администратора!
        return true;
    }
}
