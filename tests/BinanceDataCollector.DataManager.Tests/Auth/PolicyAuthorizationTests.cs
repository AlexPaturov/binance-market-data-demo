using System.Security.Claims;
using BinanceDataCollector.DataManager.Common.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace BinanceDataCollector.DataManager.Tests.Auth;

/// <summary>
/// Behavioral verification of the authorization policies: builds a real
/// <see cref="IAuthorizationService"/> and evaluates each policy against a
/// principal produced by the real <see cref="IdentityProviderRoleClaimsTransformation"/>,
/// so the full chain B2C role claim -> role -> policy allow/deny is exercised.
/// </summary>
public class PolicyAuthorizationTests
{
    [Theory]
    // dataManagerRole claim | Viewer policy | Operator policy | Admin policy
    [InlineData(null, true, false, false)]                       // no claim -> Viewer default
    [InlineData(DataManagerRoles.Viewer, true, false, false)]
    [InlineData(DataManagerRoles.Operator, true, true, false)]   // Operator: mutations allowed, Admin denied
    [InlineData(DataManagerRoles.Admin, true, true, true)]
    public async Task Policies_GrantExpectedAccess_PerRole(
        string? dataManagerRole, bool viewerAllowed, bool operatorAllowed, bool adminAllowed)
    {
        var authService = CreateAuthorizationService();
        var principal = await PrincipalForB2cRoleClaimAsync(dataManagerRole);

        Assert.Equal(viewerAllowed,
            (await authService.AuthorizeAsync(principal, DataManagerAuthorizationPolicies.Viewer)).Succeeded);
        Assert.Equal(operatorAllowed,
            (await authService.AuthorizeAsync(principal, DataManagerAuthorizationPolicies.Operator)).Succeeded);
        Assert.Equal(adminAllowed,
            (await authService.AuthorizeAsync(principal, DataManagerAuthorizationPolicies.Admin)).Succeeded);
    }

    [Fact]
    public async Task OperatorPolicy_DeniesAnonymousUser()
    {
        var authService = CreateAuthorizationService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authService.AuthorizeAsync(anonymous, DataManagerAuthorizationPolicies.Operator);

        Assert.False(result.Succeeded);
    }

    // Policy definitions mirror AddAuthorization in
    // src/BinanceDataCollector.DataManager/Program.cs — keep them in sync.
    private static IAuthorizationService CreateAuthorizationService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(DataManagerAuthorizationPolicies.Viewer, policy =>
                policy.RequireRole(DataManagerRoles.Viewer, DataManagerRoles.Operator, DataManagerRoles.Admin));
            options.AddPolicy(DataManagerAuthorizationPolicies.Operator, policy =>
                policy.RequireRole(DataManagerRoles.Operator, DataManagerRoles.Admin));
            options.AddPolicy(DataManagerAuthorizationPolicies.Admin, policy =>
                policy.RequireRole(DataManagerRoles.Admin));
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static async Task<ClaimsPrincipal> PrincipalForB2cRoleClaimAsync(string? dataManagerRole)
    {
        var claims = dataManagerRole is null
            ? Array.Empty<Claim>()
            : new[] { new Claim("extension_DataManagerRole", dataManagerRole) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        return await new IdentityProviderRoleClaimsTransformation().TransformAsync(principal);
    }
}
