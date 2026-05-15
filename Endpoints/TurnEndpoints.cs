using Workspace.Dtos.Calls;
using Workspace.Services;

namespace Workspace.Endpoints;

public static class TurnEndpoints
{
    // Conservative default — long enough to outlast a typical call, short enough
    // that a leaked credential is useless within an hour.
    private const int DefaultTtlSeconds = 3600;

    public static RouteGroupBuilder MapTurnEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/turn").WithTags("Turn");

        group.MapGet("/credentials", GetCredentialsAsync)
            .WithName("GetTurnCredentials")
            .WithSummary("Mint short-lived ICE-server credentials (STUN + TURN) for a WebRTC call")
            .Produces<TurnCredentialsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireRateLimiting("public-per-ip");

        return group;
    }

    private static async Task<IResult> GetCredentialsAsync(
        ITurnService turnService,
        CancellationToken ct)
    {
        var creds = await turnService.MintCredentialsAsync(DefaultTtlSeconds, ct);
        if (creds is null)
        {
            return Results.Problem(
                title: "TURN credentials unavailable",
                detail: "Could not mint TURN credentials right now. Clients should fall back to STUN-only ICE.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "turn_unavailable");
        }

        return Results.Ok(creds);
    }
}
