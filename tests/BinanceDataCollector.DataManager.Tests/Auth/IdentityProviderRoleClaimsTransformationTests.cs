using System.Security.Claims;
using BinanceDataCollector.DataManager.Common.Auth;

namespace BinanceDataCollector.DataManager.Tests.Auth;

public class IdentityProviderRoleClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_AddsViewerRole_WhenAuthenticatedUserHasNoRoleClaim()
    {
        var principal = CreateAuthenticatedPrincipal();
        var transformation = new IdentityProviderRoleClaimsTransformation();

        var transformed = await transformation.TransformAsync(principal);

        Assert.True(transformed.IsInRole(DataManagerRoles.Viewer));
        Assert.False(transformed.IsInRole(DataManagerRoles.Operator));
        Assert.False(transformed.IsInRole(DataManagerRoles.Admin));
    }

    [Fact]
    public async Task TransformAsync_AddsRole_FromB2cDataManagerRoleExtensionClaim()
    {
        var principal = CreateAuthenticatedPrincipal(new Claim(
            "extension_a481ca94ac8a43f599f4ef51ac345e7e_DataManagerRole",
            DataManagerRoles.Admin));
        var transformation = new IdentityProviderRoleClaimsTransformation();

        var transformed = await transformation.TransformAsync(principal);

        Assert.True(transformed.IsInRole(DataManagerRoles.Admin));
        Assert.False(transformed.IsInRole(DataManagerRoles.Viewer));
    }

    [Fact]
    public async Task TransformAsync_SplitsMultipleRoles_FromProviderClaim()
    {
        var principal = CreateAuthenticatedPrincipal(new Claim(
            "roles",
            $"{DataManagerRoles.Viewer} {DataManagerRoles.Operator}"));
        var transformation = new IdentityProviderRoleClaimsTransformation();

        var transformed = await transformation.TransformAsync(principal);

        Assert.True(transformed.IsInRole(DataManagerRoles.Viewer));
        Assert.True(transformed.IsInRole(DataManagerRoles.Operator));
        Assert.False(transformed.IsInRole(DataManagerRoles.Admin));
    }

    [Fact]
    public async Task TransformAsync_DoesNotAddRole_WhenPrincipalIsAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var transformation = new IdentityProviderRoleClaimsTransformation();

        var transformed = await transformation.TransformAsync(principal);

        Assert.Empty(transformed.Claims.Where(claim => claim.Type == ClaimTypes.Role));
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }
}
