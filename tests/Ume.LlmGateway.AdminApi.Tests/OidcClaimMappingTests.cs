using System.Security.Claims;
using Ume.LlmGateway.AdminApi;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class OidcClaimMappingTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static string[] Values(ClaimsPrincipal user, string type) => [.. user.FindAll(type).Select(c => c.Value)];

    [Fact]
    public void Defaults_expand_json_arrays_like_the_bundled_realm()
    {
        var user = Principal(("roles", "[\"viewer\",\"gateway-admin\"]"), ("departmentCodes", "[\"KS\"]"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcClaimOptions());
        Values(user, "roles").ShouldBe(["viewer", "gateway-admin"], ignoreOrder: true);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Renamed_claims_are_copied_to_the_names_the_api_uses()
    {
        var user = Principal(("role", "viewer"), ("department", "KS"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcClaimOptions { RoleClaim = "role", DepartmentClaim = "department" });
        Values(user, "roles").ShouldBe(["viewer"]);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Groups_grant_roles_case_insensitively_and_unmapped_groups_grant_nothing()
    {
        var options = new OidcClaimOptions
        {
            RoleGroups = new() { ["gateway-admin"] = "GG-Llm-Admins; GG-Llm-Ops", ["viewer"] = "GG-Llm-Viewers" },
        };
        var user = Principal(("groups", "[\"gg-llm-ops\",\"GG-Other\"]"));
        AdminAuthentication.ApplyClaimMapping(user, options);
        Values(user, "roles").ShouldBe(["gateway-admin"]);
    }

    [Fact]
    public void A_user_without_matching_claims_gets_no_roles()
    {
        var user = Principal(("groups", "GG-Other"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcClaimOptions { RoleGroups = new() { ["viewer"] = "GG-Llm-Viewers" } });
        Values(user, "roles").ShouldBeEmpty();
    }
}
