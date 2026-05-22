using System.Net;
using System.Net.Http.Json;
using Workspace.Api.Tests.Infrastructure;
using Workspace.Dtos.Admin;
using Workspace.Dtos.Calls;
using Workspace.Dtos.Contacts;
using Workspace.Dtos.Invites;
using Workspace.Dtos.Usage;
using AdminUsageAdjustmentRequest = Workspace.Dtos.Admin.UsageAdjustmentRequest;
using Workspace.Dtos.Users;

namespace Workspace.Api.Tests;

public sealed class ApiIntegrationTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    [Fact]
    public async Task Register_ByCode_AddContact_ListContacts_ShouldSucceed()
    {
        using var client = factory.CreateClient();

        var userA = await RegisterUserAsync(client, "Alice");
        var userB = await RegisterUserAsync(client, "Bob");

        Assert.Equal(8, userA.Code.Length);
        Assert.Equal(8, userB.Code.Length);
        Assert.NotEqual(userA.UserId, userB.UserId);

        var byCodeResponse = await client.GetAsync($"/api/users/by-code/{userB.Code}");
        byCodeResponse.AssertStatus(HttpStatusCode.OK);
        var byCode = await byCodeResponse.ReadRequiredAsync<UserByCodeResponse>();
        Assert.Equal(userB.UserId, byCode.UserId);
        Assert.Equal("Bob", byCode.DisplayName);

        var addContact = await client.PostAsJsonAsync("/api/contacts/add", new AddContactRequest
        {
            OwnerUserId = userA.UserId,
            ContactCode = userB.Code
        });
        addContact.AssertStatus(HttpStatusCode.Created);

        var listContacts = await client.GetAsync($"/api/contacts/{userA.UserId}");
        listContacts.AssertStatus(HttpStatusCode.OK);
        var contacts = await listContacts.ReadRequiredAsync<List<ContactItemResponse>>();
        Assert.Single(contacts);
        Assert.Equal(userB.UserId, contacts[0].UserId);
        Assert.Equal("Bob", contacts[0].DisplayName);
    }

    [Fact]
    public async Task Start_Join_End_Call_ShouldBillCreatorAndUpdateUsage()
    {
        using var client = factory.CreateClient();

        var creator = await RegisterUserAsync(client, "CallCreator");
        var callee = await RegisterUserAsync(client, "CallCallee");

        var start = await client.PostAsJsonAsync("/api/calls/start", new StartCallRequest
        {
            CreatedByUserId = creator.UserId,
            CalleeUserId = callee.UserId,
            Provider = "internal",
            ProviderRoomId = "room-123"
        });
        start.AssertStatus(HttpStatusCode.OK);
        var started = await start.ReadRequiredAsync<StartCallResponse>();

        var join = await client.PostAsJsonAsync($"/api/calls/{started.CallId}/join", new JoinCallRequest
        {
            UserId = callee.UserId
        });
        join.AssertStatus(HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var end = await client.PostAsJsonAsync($"/api/calls/{started.CallId}/end", new EndCallRequest
        {
            EndedByUserId = creator.UserId
        });
        end.AssertStatus(HttpStatusCode.OK);
        var ended = await end.ReadRequiredAsync<EndCallResponse>();
        Assert.True(ended.BilledSeconds > 0);
        Assert.True(ended.UsedSeconds >= ended.BilledSeconds);

        var month = DateTime.UtcNow.Year * 100 + DateTime.UtcNow.Month;
        var usage = await client.GetAsync($"/api/usage/{creator.UserId}/month/{month}");
        usage.AssertStatus(HttpStatusCode.OK);
        var usageBody = await usage.ReadRequiredAsync<UsageResponse>();
        Assert.True(usageBody.UsedSeconds > 0);
    }

    [Fact]
    public async Task QuotaExceeded_ShouldReturnForbiddenOnCallStart()
    {
        using var client = factory.CreateClient();

        var creator = await RegisterUserAsync(client, "QuotaCreator");
        var callee = await RegisterUserAsync(client, "QuotaCallee");
        var month = DateTime.UtcNow.Year * 100 + DateTime.UtcNow.Month;

        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiTestFactory.TestAdminKey);

        var setLimit = await client.PatchAsJsonAsync(
            $"/api/admin/users/{creator.UserId}/limit",
            new UpdateUserLimitRequest(1, null));
        setLimit.AssertStatus(HttpStatusCode.OK);

        var adjustment = await client.PostAsJsonAsync("/api/admin/usage-adjustments", new AdminUsageAdjustmentRequest(
            creator.UserId,
            month,
            5,
            "test-over-quota"));
        adjustment.AssertStatus(HttpStatusCode.OK);

        var start = await client.PostAsJsonAsync("/api/calls/start", new StartCallRequest
        {
            CreatedByUserId = creator.UserId,
            CalleeUserId = callee.UserId
        });
        start.AssertStatus(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminEndpoints_ShouldRequireApiKey()
    {
        using var client = factory.CreateClient();
        var user = await RegisterUserAsync(client, "AdminTarget");

        var noKey = await client.PatchAsJsonAsync(
            $"/api/admin/users/{user.UserId}/limit",
            new UpdateUserLimitRequest(1200, null));
        noKey.AssertStatus(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Add("X-Admin-Key", ApiTestFactory.TestAdminKey);

        var withKey = await client.PatchAsJsonAsync(
            $"/api/admin/users/{user.UserId}/limit",
            new UpdateUserLimitRequest(1200, null));
        withKey.AssertStatus(HttpStatusCode.OK);
        var updated = await withKey.ReadRequiredAsync<UpdateUserLimitResponse>();
        Assert.Equal(1200, updated.MonthlyLimitSeconds);
    }

    [Fact]
    public async Task AutoDeleteSetting_ShouldDefaultToOneHourAndAllowStringAndNumericUpdates()
    {
        using var client = factory.CreateClient();
        var user = await RegisterUserAsync(client, "AutoDeleteUser");

        var defaultSetting = await client.GetAsync($"/api/users/{user.UserId}/auto-delete-setting");
        defaultSetting.AssertStatus(HttpStatusCode.OK);
        var defaultBody = await defaultSetting.ReadRequiredAsync<AutoDeleteCallHistorySettingResponse>();
        Assert.Equal("OneHour", defaultBody.AutoDeleteMode);

        var updateToNever = await client.PostAsJsonAsync("/api/users/auto-delete-setting", new
        {
            userId = user.UserId,
            mode = "Never"
        });
        updateToNever.AssertStatus(HttpStatusCode.OK);
        var neverBody = await updateToNever.ReadRequiredAsync<UpdateAutoDeleteCallHistorySettingResponse>();
        Assert.True(neverBody.Success);
        Assert.Equal("Never", neverBody.AutoDeleteMode);

        var updateToSixHours = await client.PostAsJsonAsync("/api/users/auto-delete-setting", new
        {
            userId = user.UserId,
            mode = 2
        });
        updateToSixHours.AssertStatus(HttpStatusCode.OK);
        var sixHoursBody = await updateToSixHours.ReadRequiredAsync<UpdateAutoDeleteCallHistorySettingResponse>();
        Assert.Equal("SixHours", sixHoursBody.AutoDeleteMode);
    }

    [Fact]
    public async Task AutoDeleteSetting_ShouldRejectInvalidModeAndMissingUser()
    {
        using var client = factory.CreateClient();
        var user = await RegisterUserAsync(client, "InvalidAutoDeleteUser");

        var invalid = await client.PostAsJsonAsync("/api/users/auto-delete-setting", new
        {
            userId = user.UserId,
            mode = "Tomorrowish"
        });
        invalid.AssertStatus(HttpStatusCode.BadRequest);

        var missing = await client.PostAsJsonAsync("/api/users/auto-delete-setting", new
        {
            userId = Guid.NewGuid(),
            mode = "OneHour"
        });
        missing.AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invite_CreateRedeem_ShouldAddMutualContactsAndRejectReuse()
    {
        using var client = factory.CreateClient();

        var owner = await RegisterUserAsync(client, "InviteOwner");
        var redeemer = await RegisterUserAsync(client, "InviteRedeemer");

        client.DefaultRequestHeaders.Add("X-User-Id", owner.UserId.ToString());
        var create = await client.PostAsJsonAsync("/api/invites/create", new CreateInviteRequest
        {
            OwnerUserId = owner.UserId
        });
        create.AssertStatus(HttpStatusCode.Created);
        var created = await create.ReadRequiredAsync<CreateInviteResponse>();
        Assert.Equal(10, created.InviteCode.Length);
        Assert.True(created.ExpiresAtUtc > DateTimeOffset.UtcNow);

        client.DefaultRequestHeaders.Remove("X-User-Id");
        client.DefaultRequestHeaders.Add("X-User-Id", redeemer.UserId.ToString());
        var redeem = await client.PostAsJsonAsync("/api/invites/redeem", new RedeemInviteRequest
        {
            RedeemerUserId = redeemer.UserId,
            InviteCode = created.InviteCode
        });
        redeem.AssertStatus(HttpStatusCode.OK);
        var redeemed = await redeem.ReadRequiredAsync<RedeemInviteResponse>();
        Assert.True(redeemed.Success);
        Assert.Equal(owner.UserId, redeemed.Contact?.UserId);

        var ownerContacts = await client.GetAsync($"/api/contacts/{owner.UserId}");
        ownerContacts.AssertStatus(HttpStatusCode.OK);
        var ownerContactsBody = await ownerContacts.ReadRequiredAsync<List<ContactItemResponse>>();
        Assert.Contains(ownerContactsBody, x => x.UserId == redeemer.UserId);

        var redeemerContacts = await client.GetAsync($"/api/contacts/{redeemer.UserId}");
        redeemerContacts.AssertStatus(HttpStatusCode.OK);
        var redeemerContactsBody = await redeemerContacts.ReadRequiredAsync<List<ContactItemResponse>>();
        Assert.Contains(redeemerContactsBody, x => x.UserId == owner.UserId);

        var reuse = await client.PostAsJsonAsync("/api/invites/redeem", new RedeemInviteRequest
        {
            RedeemerUserId = redeemer.UserId,
            InviteCode = created.InviteCode
        });
        reuse.AssertStatus(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invite_Revoke_ShouldHideFromActiveListAndBlockRedemption()
    {
        using var client = factory.CreateClient();

        var owner = await RegisterUserAsync(client, "RevokeOwner");
        var redeemer = await RegisterUserAsync(client, "RevokeRedeemer");

        client.DefaultRequestHeaders.Add("X-User-Id", owner.UserId.ToString());
        var create = await client.PostAsJsonAsync("/api/invites/create", new CreateInviteRequest
        {
            OwnerUserId = owner.UserId,
            MaxRedemptions = 2
        });
        create.AssertStatus(HttpStatusCode.Created);
        var created = await create.ReadRequiredAsync<CreateInviteResponse>();

        var activeBefore = await client.GetAsync($"/api/invites/active/{owner.UserId}");
        activeBefore.AssertStatus(HttpStatusCode.OK);
        var activeBeforeBody = await activeBefore.ReadRequiredAsync<List<ActiveInviteResponse>>();
        var invite = Assert.Single(activeBeforeBody);

        var revoke = await client.PostAsJsonAsync($"/api/invites/{invite.Id}/revoke", new RevokeInviteRequest
        {
            OwnerUserId = owner.UserId
        });
        revoke.AssertStatus(HttpStatusCode.OK);

        var activeAfter = await client.GetAsync($"/api/invites/active/{owner.UserId}");
        activeAfter.AssertStatus(HttpStatusCode.OK);
        var activeAfterBody = await activeAfter.ReadRequiredAsync<List<ActiveInviteResponse>>();
        Assert.Empty(activeAfterBody);

        client.DefaultRequestHeaders.Remove("X-User-Id");
        client.DefaultRequestHeaders.Add("X-User-Id", redeemer.UserId.ToString());
        var redeem = await client.PostAsJsonAsync("/api/invites/redeem", new RedeemInviteRequest
        {
            RedeemerUserId = redeemer.UserId,
            InviteCode = created.InviteCode
        });
        redeem.AssertStatus(HttpStatusCode.Gone);
    }

    private static async Task<RegisterUserResponse> RegisterUserAsync(HttpClient client, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/users/register", new RegisterUserRequest
        {
            DisplayName = displayName
        });
        response.AssertStatus(HttpStatusCode.Created);
        return await response.ReadRequiredAsync<RegisterUserResponse>();
    }
}
