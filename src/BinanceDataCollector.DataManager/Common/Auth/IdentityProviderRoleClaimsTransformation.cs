using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace BinanceDataCollector.DataManager.Common.Auth;

public sealed class IdentityProviderRoleClaimsTransformation : IClaimsTransformation
{
    private static readonly string[] SourceRoleClaimTypes =
    [
        ClaimTypes.Role,
        "roles",
        "role",
        "extension_Role",
        "extension_Roles",
        "extension_AppRole",
        "extension_AppRoles",
        "extension_DataManagerRole",
        "extension_DataManagerRoles"
    ];

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(principal);
        }

        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null)
        {
            return Task.FromResult(principal);
        }

        var roles = GetIdentityProviderRoles(principal).ToArray();
        if (roles.Length == 0)
        {
            roles = [DataManagerRoles.Viewer];
        }

        foreach (var role in roles)
        {
            if (!identity.HasClaim(identity.RoleClaimType, role))
            {
                identity.AddClaim(new Claim(identity.RoleClaimType, role));
            }
        }

        return Task.FromResult(principal);
    }

    private static IEnumerable<string> GetIdentityProviderRoles(ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(claim => IsRoleClaimType(claim.Type))
            .SelectMany(SplitRoleClaimValue)
            .Select(role => role.Trim())
            .Where(role => DataManagerRoles.All.Contains(role))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRoleClaimType(string claimType)
    {
        return SourceRoleClaimTypes.Contains(claimType) ||
               claimType.EndsWith("_DataManagerRole", StringComparison.OrdinalIgnoreCase) ||
               claimType.EndsWith("_DataManagerRoles", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitRoleClaimValue(Claim claim)
    {
        return claim.Value.Split(
            new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
