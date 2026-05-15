using System.ComponentModel.DataAnnotations;

namespace Workspace.Dtos.Users;

public sealed class RegisterPushTokenRequest
{
    [Required]
    [StringLength(512, MinimumLength = 8)]
    public string Token { get; init; } = string.Empty;

    [StringLength(128)]
    public string? DeviceId { get; init; }

    [StringLength(16)]
    public string Platform { get; init; } = "android";
}

public sealed record PushTokenResponse(Guid Id, Guid UserId, string Platform, DateTime LastSeenAt);
