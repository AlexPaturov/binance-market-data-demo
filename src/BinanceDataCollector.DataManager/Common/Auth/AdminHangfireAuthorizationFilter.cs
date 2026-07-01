using Hangfire.Annotations;
using Hangfire.Dashboard;

namespace BinanceDataCollector.DataManager.Common.Auth;

public sealed class AdminHangfireAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize([NotNull] DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        return httpContext.User.Identity?.IsAuthenticated == true &&
               httpContext.User.IsInRole(DataManagerRoles.Admin);
    }
}
