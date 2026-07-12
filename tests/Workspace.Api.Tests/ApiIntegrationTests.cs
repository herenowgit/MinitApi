using System.Net;
using System.Net.Http.Json;
using Workspace.Api.Tests.Infrastructure;
using Workspace.Dtos.Admin;
using Workspace.Dtos.Calls;
using Workspace.Dtos.Contacts;
using Workspace.Dtos.Invites;
using Workspace.Dtos.Messages;
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

    [Fact]
    public async Task Messages_SendConversationAndRecentConversations_ShouldSucceed()
    {
        using var client = factory.CreateClient();

        var userA = await RegisterUserAsync(client, "MessageAlice");
        var userB = await RegisterUserAsync(client, "MessageBob");

        var first = await client.PostAsJsonAsync("/api/messages/send", new SendMessageRequest
        {
            SenderUserId = userA.UserId,
            ReceiverUserId = userB.UserId,
            EncryptedMessage = "ZW5jcnlwdGVkLW1lc3NhZ2UtMQ==",
            EncryptedKey = "d2NmFwcGVkLWtleS1mb3ItYm9i",
            EncryptedKeyForSender = "d3JhcHBlZC1rZXktZm9yLWFsaWNl",
            Iv = "aXYtb25lLTEyYg=="
        });
        first.AssertStatus(HttpStatusCode.Created);
        var firstBody = await first.ReadRequiredAsync<MessageResponse>();
        Assert.Equal(userA.UserId, firstBody.SenderUserId);
        Assert.Equal(userB.UserId, firstBody.ReceiverUserId);
        Assert.Equal("ZW5jcnlwdGVkLW1lc3NhZ2UtMQ==", firstBody.EncryptedMessage);
        Assert.Equal("aXYtb25lLTEyYg==", firstBody.Iv);

        var second = await client.PostAsJsonAsync("/api/messages/send", new SendMessageRequest
        {
            SenderUserId = userB.UserId,
            ReceiverUserId = userA.UserId,
            EncryptedMessage = "ZW5jcnlwdGVkLW1lc3NhZ2UtMg==",
            EncryptedKey = "d3JhcHBlZC1rZXktZm9yLWFsaWNlLTI=",
            EncryptedKeyForSender = "d3JhcHBlZC1rZXktZm9yLWJvYi0y",
            Iv = "aXYtdHdvLTEyYnl0"
        });
        second.AssertStatus(HttpStatusCode.Created);
        var secondBody = await second.ReadRequiredAsync<MessageResponse>();

        var conversation = await client.GetAsync($"/api/messages/conversation?userId={userA.UserId}&otherUserId={userB.UserId}&page=1&pageSize=50");
        conversation.AssertStatus(HttpStatusCode.OK);
        var conversationBody = await conversation.ReadRequiredAsync<List<MessageResponse>>();
        Assert.Equal(2, conversationBody.Count);
        Assert.Equal(secondBody.Id, conversationBody[0].Id);
        Assert.Equal(firstBody.Id, conversationBody[1].Id);

        var recent = await client.GetAsync($"/api/messages/conversations/{userA.UserId}");
        recent.AssertStatus(HttpStatusCode.OK);
        var recentBody = await recent.ReadRequiredAsync<List<RecentConversationResponse>>();
        var recentConversation = Assert.Single(recentBody);
        Assert.Equal(userB.UserId, recentConversation.OtherUserId);
        Assert.Equal(secondBody.Id, recentConversation.LastMessage.Id);
    }

    [Fact]
    public async Task Messages_DeleteChat_ShouldScopeToConversationAndMode()
    {
        using var client = factory.CreateClient();

        var alice = await RegisterUserAsync(client, "DeleteAlice");
        var bob = await RegisterUserAsync(client, "DeleteBob");
        var carol = await RegisterUserAsync(client, "DeleteCarol");

        // Alice→Bob, Bob→Alice, and an unrelated Alice→Carol message that must survive.
        await SendEncryptedAsync(client, alice.UserId, bob.UserId);
        await SendEncryptedAsync(client, bob.UserId, alice.UserId);
        await SendEncryptedAsync(client, alice.UserId, carol.UserId);

        // DeleteMine: removes only Alice's messages to Bob; Bob→Alice remains.
        var deleteMine = await client.PostAsJsonAsync("/api/messages/delete-chat", new DeleteChatRequest
        {
            UserId = alice.UserId,
            OtherUserId = bob.UserId,
            Mode = "DeleteMine"
        });
        deleteMine.AssertStatus(HttpStatusCode.OK);
        var deleteMineBody = await deleteMine.ReadRequiredAsync<DeleteChatResponse>();
        Assert.Equal(1, deleteMineBody.DeletedCount);

        var afterMine = await client.GetAsync($"/api/messages/conversation?userId={alice.UserId}&otherUserId={bob.UserId}");
        afterMine.AssertStatus(HttpStatusCode.OK);
        var afterMineBody = await afterMine.ReadRequiredAsync<List<MessageResponse>>();
        var remaining = Assert.Single(afterMineBody);
        Assert.Equal(bob.UserId, remaining.SenderUserId);

        // DeleteAll: removes the remaining Bob→Alice message too.
        var deleteAll = await client.PostAsJsonAsync("/api/messages/delete-chat", new DeleteChatRequest
        {
            UserId = alice.UserId,
            OtherUserId = bob.UserId,
            Mode = "DeleteAll"
        });
        deleteAll.AssertStatus(HttpStatusCode.OK);
        var deleteAllBody = await deleteAll.ReadRequiredAsync<DeleteChatResponse>();
        Assert.Equal(1, deleteAllBody.DeletedCount);

        var afterAll = await client.GetAsync($"/api/messages/conversation?userId={alice.UserId}&otherUserId={bob.UserId}");
        afterAll.AssertStatus(HttpStatusCode.OK);
        var afterAllBody = await afterAll.ReadRequiredAsync<List<MessageResponse>>();
        Assert.Empty(afterAllBody);

        // The unrelated Alice→Carol conversation is untouched.
        var carolConversation = await client.GetAsync($"/api/messages/conversation?userId={alice.UserId}&otherUserId={carol.UserId}");
        carolConversation.AssertStatus(HttpStatusCode.OK);
        var carolBody = await carolConversation.ReadRequiredAsync<List<MessageResponse>>();
        Assert.Single(carolBody);
    }

    [Fact]
    public async Task DeleteAccount_ShouldRemoveUserAndAllRelatedData_AndBeIdempotent()
    {
        using var client = factory.CreateClient();

        var alice = await RegisterUserAsync(client, "AliceDel");
        var bob = await RegisterUserAsync(client, "BobDel");

        // Mutual contacts (Alice is ContactUserId in Bob's list — a Restrict FK).
        (await client.PostAsJsonAsync("/api/contacts/add", new AddContactRequest
        {
            OwnerUserId = alice.UserId,
            ContactCode = bob.Code
        })).AssertStatus(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/contacts/add", new AddContactRequest
        {
            OwnerUserId = bob.UserId,
            ContactCode = alice.Code
        })).AssertStatus(HttpStatusCode.Created);

        // Messages both directions + a call Alice created (Restrict FKs).
        await SendEncryptedAsync(client, alice.UserId, bob.UserId);
        await SendEncryptedAsync(client, bob.UserId, alice.UserId);
        (await client.PostAsJsonAsync("/api/calls/start", new StartCallRequest
        {
            CreatedByUserId = alice.UserId,
            CalleeUserId = bob.UserId
        })).AssertStatus(HttpStatusCode.OK);

        // Delete Alice.
        var delete = await client.DeleteAsync($"/api/users/{alice.UserId}");
        delete.AssertStatus(HttpStatusCode.NoContent);

        // Alice no longer exists.
        (await client.PostAsync($"/api/users/{alice.UserId}/heartbeat", null))
            .AssertStatus(HttpStatusCode.NotFound);

        // Alice was removed from Bob's contact list (ContactUserId Restrict FK cleared).
        var bobContacts = await client.GetAsync($"/api/contacts/{bob.UserId}");
        bobContacts.AssertStatus(HttpStatusCode.OK);
        Assert.Empty(await bobContacts.ReadRequiredAsync<List<ContactItemResponse>>());

        // All of Bob's conversations with Alice are gone (messages deleted both ways).
        var bobConversations = await client.GetAsync($"/api/messages/conversations/{bob.UserId}");
        bobConversations.AssertStatus(HttpStatusCode.OK);
        Assert.Empty(await bobConversations.ReadRequiredAsync<List<RecentConversationResponse>>());

        // Deleting again is idempotent → 404.
        (await client.DeleteAsync($"/api/users/{alice.UserId}")).AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Heartbeat_ShouldReturnOkForExistingUser_And404ForUnknown()
    {
        using var client = factory.CreateClient();

        var user = await RegisterUserAsync(client, "HeartbeatUser");

        (await client.PostAsync($"/api/users/{user.UserId}/heartbeat", null))
            .AssertStatus(HttpStatusCode.OK);

        (await client.PostAsync($"/api/users/{Guid.NewGuid()}/heartbeat", null))
            .AssertStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AutoDeleteAccountSetting_ShouldRoundTrip()
    {
        using var client = factory.CreateClient();

        var user = await RegisterUserAsync(client, "AccountSettingUser");

        var initial = await client.GetAsync($"/api/users/{user.UserId}/auto-delete-account-setting");
        initial.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("Never", (await initial.ReadRequiredAsync<AutoDeleteAccountSettingResponse>()).AutoDeleteMode);

        var update = await client.PostAsJsonAsync("/api/users/auto-delete-account-setting",
            new UpdateAutoDeleteAccountSettingRequest { UserId = user.UserId, Mode = "FiveDays" });
        update.AssertStatus(HttpStatusCode.OK);
        Assert.True((await update.ReadRequiredAsync<UpdateAutoDeleteAccountSettingResponse>()).Success);

        var after = await client.GetAsync($"/api/users/{user.UserId}/auto-delete-account-setting");
        after.AssertStatus(HttpStatusCode.OK);
        Assert.Equal("FiveDays", (await after.ReadRequiredAsync<AutoDeleteAccountSettingResponse>()).AutoDeleteMode);
    }

    private static async Task SendEncryptedAsync(HttpClient client, Guid senderUserId, Guid receiverUserId)
    {
        var response = await client.PostAsJsonAsync("/api/messages/send", new SendMessageRequest
        {
            SenderUserId = senderUserId,
            ReceiverUserId = receiverUserId,
            EncryptedMessage = "ZW5jcnlwdGVkLW1lc3NhZ2U=",
            EncryptedKey = "d3JhcHBlZC1rZXktcmVjZWl2ZXI=",
            EncryptedKeyForSender = "d3JhcHBlZC1rZXktc2VuZGVy",
            Iv = "aXYtMTJieXRlcw=="
        });
        response.AssertStatus(HttpStatusCode.Created);
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
