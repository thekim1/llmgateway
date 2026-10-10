using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.DataApi.Tests;

public sealed class DataApiTests(DataApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_need_a_valid_token_with_the_right_audience_and_permission()
    {
        (await fixture.Anonymous().GetAsync("/v1/usage/records", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.Client("usage.detail", audience: "someone-else").GetAsync("/v1/usage/records", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await fixture.Client("catalog.read usage.aggregate").GetAsync("/v1/usage/records", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.Client("usage.detail").GetAsync("/v1/security/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await fixture.Client("catalog.read usage.detail").GetAsync("/v1/usage/records?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Entra ID puts application permissions in a "roles" array.
        (await fixture.Client("security.read", claim: "roles").GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The contract itself is public.
        (await fixture.Anonymous().GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Usage_feed_pages_by_cursor_until_caught_up()
    {
        var start = await fixture.UsageCursorAsync();
        var department = Guid.NewGuid();
        await fixture.DbAsync(db => { db.UsageRecords.AddRange(Enumerable.Range(0, 5).Select(_ => DataApiFixture.Usage(department))); return db.SaveChangesAsync(Ct); });
        using var client = fixture.Client("usage.detail");

        var ids = new List<long>();
        var cursor = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool hasMore;
        var pages = 0;
        do
        {
            using var page = await ReadAsync(client, $"/v1/usage/records?after={cursor}&limit=2");
            var root = page.RootElement;
            ids.AddRange(root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()));
            cursor = root.GetProperty("nextCursor").GetString()!;
            hasMore = root.GetProperty("hasMore").GetBoolean();
            pages++;
        }
        while (hasMore);

        ids.Count.ShouldBe(5);
        ids.ShouldBe(ids.Order().ToList());
        pages.ShouldBe(3);
        using var caughtUp = await ReadAsync(client, $"/v1/usage/records?after={cursor}");
        caughtUp.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        caughtUp.RootElement.GetProperty("nextCursor").GetString().ShouldBe(cursor);
    }

    [Fact]
    public async Task Feed_stops_at_a_row_that_has_not_settled()
    {
        var start = await fixture.UsageCursorAsync();
        var department = Guid.NewGuid();
        var settled = DataApiFixture.Usage(department);
        var committing = DataApiFixture.Usage(department, configure: u => u.RecordedAt = DateTimeOffset.UtcNow.AddMinutes(10));
        var after = DataApiFixture.Usage(department);
        await fixture.DbAsync(async db =>
        {
            foreach (var record in new[] { settled, committing, after })
            {
                db.UsageRecords.Add(record);
                await db.SaveChangesAsync(Ct);
            }

            return 0;
        });

        using var page = await ReadAsync(fixture.Client("usage.detail"), $"/v1/usage/records?after={start}");
        var items = page.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        items.ShouldBe([settled.Id]); // not `after`, although it has settled: the cursor must not pass `committing`
        page.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        page.RootElement.GetProperty("nextCursor").GetString().ShouldBe(settled.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Settle the row; the next poll continues where it stopped.
        await fixture.DbAsync(db => db.UsageRecords.Where(u => u.Id == committing.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RecordedAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        using var next = await ReadAsync(fixture.Client("usage.detail"), $"/v1/usage/records?after={settled.Id}");
        next.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ShouldBe([committing.Id, after.Id]);
    }

    [Fact]
    public async Task Personal_data_categories_are_only_in_the_security_feed_by_default()
    {
        var start = await fixture.UsageCursorAsync();
        var department = Guid.NewGuid();
        var pii = DataApiFixture.Usage(department, configure: u => { u.PiiActionApplied = PiiPolicy.Redact; u.PiiCategories = "Email:1"; });
        var refused = DataApiFixture.Usage(department, configure: u => { u.ErrorCode = "model_not_allowed"; u.Outcome = RequestOutcome.Rejected; u.StatusCode = 403; });
        var ordinary = DataApiFixture.Usage(department);
        await fixture.DbAsync(db => { db.UsageRecords.AddRange(pii, refused, ordinary); return db.SaveChangesAsync(Ct); });

        using var detail = await ReadAsync(fixture.Client("usage.detail"), $"/v1/usage/records?after={start}");
        var detailItems = detail.RootElement.GetProperty("items").EnumerateArray().ToList();
        detailItems.Count.ShouldBe(3);
        detailItems.ShouldAllBe(i => i.GetProperty("piiCategories").ValueKind == JsonValueKind.Null);
        detailItems[0].GetProperty("piiAction").GetString().ShouldBe("Redact");

        using var security = await ReadAsync(fixture.Client("security.read"), $"/v1/security/requests?after={start}");
        var securityItems = security.RootElement.GetProperty("items").EnumerateArray().ToList();
        securityItems.Select(i => i.GetProperty("id").GetInt64()).ShouldBe([pii.Id, refused.Id]);
        securityItems[0].GetProperty("piiCategories").GetString().ShouldBe("Email:1");
    }

    [Fact]
    public async Task Aggregates_use_local_days_and_hide_small_departments()
    {
        // 2020-03-15 in Stockholm (UTC+1). The last record is 23:30 UTC, i.e. 00:30 on the 16th locally.
        var large = new Department { Name = "Large dept", CostCenterCode = "AGG-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        var small = new Department { Name = "Small dept", CostCenterCode = "AGG-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        var day = new DateTimeOffset(2020, 3, 15, 10, 0, 0, TimeSpan.Zero);
        await fixture.DbAsync(db =>
        {
            db.Departments.AddRange(large, small);
            db.UsageRecords.AddRange(
                DataApiFixture.Usage(large.Id, at: day), DataApiFixture.Usage(large.Id, at: day), DataApiFixture.Usage(large.Id, at: day),
                DataApiFixture.Usage(small.Id, key: Guid.NewGuid(), at: day),
                DataApiFixture.Usage(large.Id, at: new DateTimeOffset(2020, 3, 15, 23, 30, 0, TimeSpan.Zero)));
            return db.SaveChangesAsync(Ct);
        });
        using var client = fixture.Client("usage.aggregate");

        using var daily = await ReadAsync(client, "/v1/usage/aggregate?from=2020-03-15&to=2020-03-15");
        var rows = daily.RootElement.GetProperty("items").EnumerateArray().ToList();
        var largeRow = rows.Single(r => r.GetProperty("departmentId").ValueKind != JsonValueKind.Null);
        largeRow.GetProperty("departmentId").GetGuid().ShouldBe(large.Id);
        largeRow.GetProperty("costCenterCode").GetString().ShouldBe(large.CostCenterCode);
        largeRow.GetProperty("requests").GetInt64().ShouldBe(3);
        largeRow.GetProperty("costSek").GetDecimal().ShouldBe(4.5m);
        largeRow.GetProperty("period").GetString().ShouldBe("2020-03-15");
        var other = rows.Single(r => r.GetProperty("departmentId").ValueKind == JsonValueKind.Null);
        other.GetProperty("requests").GetInt64().ShouldBe(1);
        other.GetProperty("departmentName").ValueKind.ShouldBe(JsonValueKind.Null);
        daily.RootElement.GetRawText().ShouldNotContain(small.Id.ToString());

        using var monthly = await ReadAsync(client, "/v1/usage/aggregate?from=2020-03-01&to=2020-03-31&granularity=month");
        monthly.RootElement.GetProperty("items").EnumerateArray().Sum(r => r.GetProperty("requests").GetInt64()).ShouldBe(5);

        (await client.GetAsync("/v1/usage/aggregate?from=2020-01-01&to=2023-01-01", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Csv_has_a_header_cursor_headers_and_defused_formulas()
    {
        var start = await fixture.DbAsync(async db => await db.AuditLog.MaxAsync(a => (long?)a.Id) ?? 0);
        await fixture.DbAsync(db =>
        {
            db.AuditLog.Add(new AuditLogEntry { Timestamp = DateTimeOffset.UtcNow, Actor = "=HYPERLINK(\"x\")", Action = "create", EntityType = "VirtualKey", EntityId = "1" });
            return db.SaveChangesAsync(Ct);
        });

        using var response = await fixture.Client("security.read").GetAsync($"/v1/security/audit?after={start}&format=csv", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Headers.GetValues("X-Next-Cursor").Single().ShouldNotBe(start.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.GetValues("X-Has-More").Single().ShouldBe("false");
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("id,timestamp,actor,action,entityType,entityId,details,recordedAt");
        lines.Length.ShouldBe(2);
        lines[1].ShouldContain(",\"'=HYPERLINK(\"\"x\"\")\",create,");
    }

    [Fact]
    public async Task Key_inventory_never_contains_secrets_and_reads_are_logged()
    {
        var team = new Team { Name = "Inventory " + Guid.NewGuid(), DepartmentId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var key = new VirtualKey
        {
            Team = team, Name = "Inventory key", Prefix = "ume-sk-Inv1", KeyHash = new string('a', 64), EncryptedSecret = "encrypted-secret-value",
            CreatedAt = DateTimeOffset.UtcNow, RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        await fixture.DbAsync(db =>
        {
            db.Departments.Add(new Department { Id = team.DepartmentId, Name = "Inventory dept", CostCenterCode = "INV-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow });
            db.VirtualKeys.Add(key);
            return db.SaveChangesAsync(Ct);
        });

        using var response = await ReadAsync(fixture.Client("security.read", client: "siem-connector"), "/v1/security/keys");
        var text = response.RootElement.GetRawText();
        text.ShouldNotContain(key.KeyHash);
        text.ShouldNotContain("encrypted-secret-value");
        var item = response.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == key.Id);
        item.GetProperty("status").GetString().ShouldBe("Revoked");
        item.GetProperty("departmentId").GetGuid().ShouldBe(team.DepartmentId);

        fixture.Logs.Text.ShouldContain("Ume.LlmGateway.Security|data.read|Data API client siem-connector read /v1/security/keys: status 200");
    }

    [Theory]
    [InlineData("SELECT \"KeyHash\" FROM \"VirtualKeys\"")]
    [InlineData("SELECT \"EncryptedSecret\" FROM \"VirtualKeys\"")]
    [InlineData("SELECT \"EncryptedCredential\" FROM \"ProviderAccounts\"")]
    [InlineData("SELECT \"BaseUrl\" FROM \"ProviderAccounts\"")]
    [InlineData("SELECT * FROM \"DataProtectionKeys\"")]
    [InlineData("INSERT INTO \"AuditLog\" (\"Timestamp\", \"Actor\", \"Action\", \"EntityType\") VALUES (now(), 'x', 'x', 'x')")]
    public async Task The_data_role_cannot_read_secrets_or_write(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(fixture.DataConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        error.SqlState.ShouldBe("42501"); // insufficient_privilege
    }

    private async Task<JsonDocument> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }
}
