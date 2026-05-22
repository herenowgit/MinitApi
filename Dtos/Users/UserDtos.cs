using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workspace.Dtos.Users;

public sealed class RegisterUserRequest
{
    [Required]
    [StringLength(80, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class RegisterUserResponse
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public int MonthlyLimitSeconds { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class UserByCodeResponse
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class UpdateAutoDeleteCallHistorySettingRequest
{
    public Guid UserId { get; init; }

    [JsonConverter(typeof(AutoDeleteCallHistoryModeInputConverter))]
    public string Mode { get; init; } = string.Empty;
}

public sealed record UpdateAutoDeleteCallHistorySettingResponse(
    bool Success,
    string AutoDeleteMode);

public sealed record AutoDeleteCallHistorySettingResponse(
    string AutoDeleteMode);

public sealed class AutoDeleteCallHistoryModeInputConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}