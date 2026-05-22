namespace Workspace.Domain;

public sealed class CallHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }

    public User? User { get; set; }
}
