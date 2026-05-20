namespace Workspace.Dtos.Calls;

public sealed record RecentCallResponse(
    Guid CallId,
    string Direction,       // "incoming" or "outgoing"
    string Status,           // "Active" | "Ended" | "Missed" | "Cancelled"
    Guid OtherUserId,
    string OtherDisplayName,
    string OtherCode,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int BilledSeconds);
