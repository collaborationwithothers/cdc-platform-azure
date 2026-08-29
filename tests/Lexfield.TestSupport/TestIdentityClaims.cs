using System.Security.Claims;

namespace Lexfield.TestSupport;

public static class TestIdentityClaims
{
    public static Claim UserIdentityType() => new("idtyp", "user");

    public static IEnumerable<Claim> WithDefaultUserIdentityType(params Claim[] additionalClaims)
    {
        if (!additionalClaims.Any(claim => claim.Type == "idtyp"))
            yield return UserIdentityType();
        foreach (var claim in additionalClaims) yield return claim;
    }
}
