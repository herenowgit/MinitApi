using Microsoft.AspNetCore.Mvc;
using Workspace.Dtos.Invites;
using Workspace.Services;

namespace Workspace.Endpoints;

public static class InviteEndpoints
{
    public static RouteGroupBuilder MapInviteEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/invites")
            .WithTags("Invites");

        group.MapPost("/create", CreateInviteAsync)
            .WithName("CreateInvite")
            .WithSummary("Create a temporary one-time contact invite code")
            .RequireRateLimiting("invites-per-user-or-ip")
            .Produces<CreateInviteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/redeem", RedeemInviteAsync)
            .WithName("RedeemInvite")
            .WithSummary("Redeem a temporary invite code and add the owner as a contact")
            .RequireRateLimiting("invites-per-user-or-ip")
            .Produces<RedeemInviteResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapPost("/{id:guid}/revoke", RevokeInviteAsync)
            .WithName("RevokeInvite")
            .WithSummary("Revoke an active invite code")
            .Produces<RevokeInviteResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/active/{ownerUserId:guid}", ListActiveInvitesAsync)
            .WithName("ListActiveInvites")
            .WithSummary("List active non-expired invites for a user")
            .Produces<IReadOnlyList<ActiveInviteResponse>>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> CreateInviteAsync(
        [FromBody] CreateInviteRequest request,
        InviteService inviteService,
        CancellationToken ct)
    {
        var result = await inviteService.CreateInviteAsync(request, ct);
        if (!result.Success)
        {
            return Problem("Invalid invite request", result.Detail!, StatusCodes.Status400BadRequest, result.Code!);
        }

        return Results.Created(
            "/api/invites/active/" + request.OwnerUserId,
            new CreateInviteResponse(result.InviteCode!, result.ExpiresAtUtc!.Value));
    }

    private static async Task<IResult> RedeemInviteAsync(
        [FromBody] RedeemInviteRequest request,
        InviteService inviteService,
        CancellationToken ct)
    {
        var result = await inviteService.RedeemInviteAsync(request, ct);
        if (!result.Success)
        {
            return Problem("Invite redemption failed", result.Detail!, result.StatusCode, result.Code!);
        }

        return Results.Ok(new RedeemInviteResponse(true, result.Contact));
    }

    private static async Task<IResult> RevokeInviteAsync(
        Guid id,
        [FromBody] RevokeInviteRequest request,
        InviteService inviteService,
        CancellationToken ct)
    {
        var result = await inviteService.RevokeInviteAsync(id, request.OwnerUserId, ct);
        if (!result.Success)
        {
            return Problem("Invite revocation failed", result.Detail!, result.StatusCode, result.Code!);
        }

        return Results.Ok(new RevokeInviteResponse(true));
    }

    private static async Task<IResult> ListActiveInvitesAsync(
        Guid ownerUserId,
        InviteService inviteService,
        CancellationToken ct)
    {
        var invites = await inviteService.ListActiveInvitesAsync(ownerUserId, ct);
        return Results.Ok(invites);
    }

    private static IResult Problem(string title, string detail, int statusCode, string code)
        => Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            type: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
