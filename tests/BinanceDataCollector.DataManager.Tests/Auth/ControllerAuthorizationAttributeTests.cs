using System.Reflection;
using BinanceDataCollector.DataManager.Common.Auth;
using BinanceDataCollector.DataManager.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BinanceDataCollector.DataManager.Tests.Auth;

public class ControllerAuthorizationAttributeTests
{
    [Theory]
    [InlineData(typeof(HomeController))]
    [InlineData(typeof(ArchiveController))]
    [InlineData(typeof(InspectorController))]
    public void ReadOnlyControllers_RequireViewerPolicy(Type controllerType)
    {
        var authorize = controllerType.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(DataManagerAuthorizationPolicies.Viewer, authorize.Policy);
    }

    [Theory]
    [InlineData(nameof(ArchiveController.DownloadArchives))]
    [InlineData(nameof(ArchiveController.ProcessArchives))]
    [InlineData(nameof(ArchiveController.TriggerSymbolUpdate))]
    [InlineData(nameof(ArchiveController.DeleteArchives))]
    public void ArchiveMutationActions_RequireOperatorPolicy(string actionName)
    {
        var action = typeof(ArchiveController).GetMethod(actionName);

        Assert.NotNull(action);
        Assert.Contains(action.GetCustomAttributes<HttpPostAttribute>(), _ => true);

        var authorize = action.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(DataManagerAuthorizationPolicies.Operator, authorize.Policy);
    }
}
