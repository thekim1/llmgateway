using System.Security.Claims;

namespace Ume.LlmGateway.AdminApi;

public static class McpEndpoints
{
    public static void MapMcp(this RouteGroupBuilder api)
    {
        var servers = api.MapGroup("/mcp/servers").RequireAuthorization("manage").WithTags("MCP servers");
        servers.MapGet("", async (McpServerService service, ClaimsPrincipal user, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(user, ct)))
            .WithName("ListMcpServers").WithSummary("MCP servers the user may see, with their status and tools waiting for approval");
        servers.MapGet("/{id:guid}", async (Guid id, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(user, id, ct)))
            .WithName("GetMcpServer");
        servers.MapPost("", async (McpServerRequest input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var server = await service.CreateAsync(user, input, ct);
            return TypedResults.Created($"/api/mcp/servers/{server.Id}", server);
        }).WithName("CreateMcpServer").WithSummary("Adds an MCP server; the credential is stored encrypted and never returned");
        servers.MapPut("/{id:guid}", async (Guid id, McpServerRequest input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(user, id, input, ct)))
            .WithName("UpdateMcpServer").WithSummary("Changes an MCP server; omit the credential to keep it");
        servers.MapDelete("/{id:guid}", async (Guid id, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await service.DeleteAsync(user, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteMcpServer").WithSummary("Deletes an MCP server with its tools and grants");
        servers.MapPost("/test", async (McpServerRequest input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.TestAsync(user, null, input, ct)))
            .WithName("TestNewMcpServer").WithSummary("Connects to an unsaved MCP server and lists its tools");
        servers.MapPost("/{id:guid}/test", async (Guid id, McpServerRequest? input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.TestAsync(user, id, input, ct)))
            .WithName("TestMcpServer").WithSummary("Connects to an MCP server, with unsaved changes if given, and lists its tools; nothing is saved");
        servers.MapPost("/{id:guid}/sync", async (Guid id, McpToolSyncService sync, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await sync.SyncNowAsync(user, id, ct)))
            .WithName("SyncMcpServer").WithSummary("Reads the server's tools now: new tools wait for approval, changed ones are quarantined");
        servers.MapGet("/{id:guid}/tools", async (Guid id, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ToolsAsync(user, id, ct)))
            .WithName("ListMcpTools").WithSummary("The server's tools with their approval state and, for changed ones, what changed");
        servers.MapPost("/{id:guid}/tools/{toolId:guid}/approve", async (Guid id, Guid toolId, ApproveToolRequest input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ApproveAsync(user, id, toolId, input, ct)))
            .WithName("ApproveMcpTool").WithSummary("Approves the tool's current definition, if it is still the reviewed one");
        servers.MapPost("/{id:guid}/tools/{toolId:guid}/reject", async (Guid id, Guid toolId, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.RejectAsync(user, id, toolId, ct)))
            .WithName("RejectMcpTool").WithSummary("Hides the tool from every key until it is approved");
        servers.MapPut("/{id:guid}/tools/{toolId:guid}/price", async (Guid id, Guid toolId, ToolPriceRequest input, McpServerService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.SetPriceAsync(user, id, toolId, input.PriceSek, ct)))
            .WithName("SetMcpToolPrice").WithSummary("Sets the price per call in SEK (null = free)");

        var grants = api.MapGroup("/keys/{id:guid}/mcp-grants").RequireAuthorization("manage").WithTags("Keys");
        grants.MapGet("", async (Guid id, McpGrantService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user, id, ct)))
            .WithName("ListMcpGrants").WithSummary("The key's MCP grants on servers the user can see");
        grants.MapPut("", async (Guid id, McpGrantRequest[] input, McpGrantService service, ClaimsPrincipal user, CancellationToken ct) =>
            TypedResults.Ok(await service.ReplaceAsync(user, id, input, ct)))
            .WithName("ReplaceMcpGrants").WithSummary("Replaces the key's MCP grants on servers the user can see");
    }
}
