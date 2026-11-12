using System.Security.Claims;
using Ume.LlmGateway.AdminApi.Security;

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
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions());
        Values(user, "roles").ShouldBe(["viewer", "gateway-admin"], ignoreOrder: true);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Renamed_claims_are_copied_to_the_names_the_api_uses()
    {
        var user = Principal(("role", "viewer"), ("department", "KS"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions { RoleClaim = "role", DepartmentClaim = "department" });
        Values(user, "roles").ShouldBe(["viewer"]);
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }

    [Fact]
    public void Groups_grant_roles_case_insensitively_and_unmapped_groups_grant_nothing()
    {
        var options = new OidcOptions
        {
            RoleGroups = new() { ["gateway-admin"] = "GG-Llm-Admins; GG-Llm-Ops", ["viewer"] = "GG-Llm-Viewers" },
        };
        var user = Principal(("groups", "[\"gg-llm-ops\",\"GG-Other\"]"));
        AdminAuthentication.ApplyClaimMapping(user, options);
        Values(user, "roles").ShouldBe(["gateway-admin"]);
    }

    [Fact]
    public void Team_codes_come_from_their_claim_or_from_group_names()
    {
        var user = Principal(("teamCodes", "[\"AI\",\"KC\"]"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions());
        Values(user, "teamCodes").ShouldBe(["AI", "KC"]);

        var grouped = Principal(("groups", "[\"GG-Llm-TeamAdmins\",\"GG-Team-AI\"]"));
        AdminAuthentication.ApplyClaimMapping(grouped, new OidcOptions { TeamClaim = "groups", RoleGroups = new() { ["team-admin"] = "GG-Llm-TeamAdmins" } });
        Values(grouped, "roles").ShouldBe(["team-admin"]);
        Values(grouped, "teamCodes").ShouldBe(["GG-Llm-TeamAdmins", "GG-Team-AI"]);
        Values(grouped, "groups").ShouldBeEmpty();
    }

    [Fact]
    public void Group_claims_are_dropped_after_mapping_so_the_session_cookie_stays_small()
    {
        // Keycloak's ume-admin client sends every group of the user, other systems' groups included.
        string[] many = [.. Enumerable.Range(0, 300).Select(i => $"GG-Group-{i}"), "ume-admins"];
        var user = Principal(("groups", System.Text.Json.JsonSerializer.Serialize(many)), ("roles", "[\"viewer\"]"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions { RoleGroups = new() { ["gateway-admin"] = "ume-admins" } });
        Values(user, "roles").ShouldBe(["viewer", "gateway-admin"], ignoreOrder: true);
        Values(user, "groups").ShouldBeEmpty();
    }

    [Fact]
    public void A_user_without_matching_claims_gets_no_roles()
    {
        var user = Principal(("groups", "GG-Other"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions { RoleGroups = new() { ["viewer"] = "GG-Llm-Viewers" } });
        Values(user, "roles").ShouldBeEmpty();
    }

    private static readonly DepartmentGroups[] Departments =
    [
        new("3000", ["IFO-medarbetare", "0b6c6c0e-1111-4222-8333-444455556666"]),
        new("4000", ["Teknik"]),
    ];

    [Fact]
    public void A_member_group_gives_the_departments_code_and_never_a_role()
    {
        var user = Principal(("departmentCodes", "KS"), ("groups", "ifo-MEDARBETARE"), ("groups", "Something else"));
        var fromGroups = AdminAuthentication.ApplyClaimMapping(user, new OidcOptions(), Departments);
        fromGroups.ShouldBe(["3000"]);
        Values(user, "departmentCodes").ShouldBe(["KS", "3000"]);
        Values(user, "roles").ShouldBeEmpty();
        Values(user, "groups").ShouldBeEmpty();
    }

    [Fact]
    public void A_code_the_claim_already_holds_is_not_added_again_and_counts_as_from_the_claim()
    {
        var user = Principal(("departmentCodes", "3000"), ("groups", "[\"IFO-medarbetare\",\"Teknik\"]"));
        var fromGroups = AdminAuthentication.ApplyClaimMapping(user, new OidcOptions(), Departments);
        fromGroups.ShouldBe(["4000"]);
        Values(user, "departmentCodes").ShouldBe(["3000", "4000"]);

        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        DepartmentMembership.Store(properties, fromGroups);
        DepartmentMembership.Sources(Values(user, "departmentCodes"), properties)
            .ShouldBe([new DepartmentSource("3000", "claim"), new DepartmentSource("4000", "group")]);
        DepartmentMembership.Sources(["KS"], null).ShouldBe([new DepartmentSource("KS", "claim")]);
    }

    [Fact]
    public void A_token_without_the_group_claim_gets_the_claims_codes_only()
    {
        // Entra above its group limit: no groups claim, only _claim_names.
        var user = Principal(("departmentCodes", "KS"), ("_claim_names", "{\"groups\":\"src1\"}"));
        AdminAuthentication.ApplyClaimMapping(user, new OidcOptions(), Departments).ShouldBeEmpty();
        Values(user, "departmentCodes").ShouldBe(["KS"]);
    }
}
