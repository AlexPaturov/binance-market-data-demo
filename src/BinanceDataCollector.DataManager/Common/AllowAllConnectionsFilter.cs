using Hangfire.Annotations;
using Hangfire.Dashboard;

namespace BinanceDataCollector.DataManager.Common;

public class AllowAllConnectionsFilter : IDashboardAuthorizationFilter
{
    public bool Authorize([NotNull] DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return httpContext.User.Identity?.IsAuthenticated == true;
    }
}
