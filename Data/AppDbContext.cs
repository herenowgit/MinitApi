using Microsoft.EntityFrameworkCore;
using Workspace.Domain;

namespace Workspace.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<CallHistory> CallHistory => Set<CallHistory>();
    public DbSet<CallSession> CallSessions => Set<CallSession>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();
    public DbSet<MonthlyUsage> MonthlyUsages => Set<MonthlyUsage>();
    public DbSet<UsageAdjustment> UsageAdjustments => Set<UsageAdjustment>();
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<OneTimeInviteCode> OneTimeInviteCodes => Set<OneTimeInviteCode>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users");
        user.HasKey(x => x.Id);
        user.Property(x => x.DisplayName).IsRequired().HasMaxLength(80);
        user.Property(x => x.Code).IsRequired().HasMaxLength(8).IsFixedLength();
        user.Property(x => x.MonthlyLimitSeconds).HasDefaultValue(6000);
        user.Property(x => x.AutoDeleteCallHistoryMode)
            .HasConversion<int>()
            .HasSentinel((AutoDeleteCallHistoryMode)0)
            .HasDefaultValue(AutoDeleteCallHistoryMode.OneHour);
        user.Property(x => x.IsActive).HasDefaultValue(true);
        user.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        user.Property(x => x.PublicKey).HasMaxLength(1024);
        user.Property(x => x.PublicKeyUpdatedAt).HasColumnType("timestamp with time zone");
        user.HasIndex(x => x.Code).IsUnique();

        var contact = modelBuilder.Entity<Contact>();
        contact.ToTable("contacts");
        contact.HasKey(x => x.Id);
        contact.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        contact.HasOne(x => x.OwnerUser)
            .WithMany(x => x.OwnedContacts)
            .HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
        contact.HasOne(x => x.ContactUser)
            .WithMany(x => x.ContactOfUsers)
            .HasForeignKey(x => x.ContactUserId)
            .OnDelete(DeleteBehavior.Restrict);
        contact.HasIndex(x => new { x.OwnerUserId, x.ContactUserId }).IsUnique();

        var callHistory = modelBuilder.Entity<CallHistory>();
        callHistory.ToTable("call_history");
        callHistory.HasKey(x => x.Id);
        callHistory.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        callHistory.Property(x => x.IsDeleted).HasDefaultValue(false);
        callHistory.Property(x => x.DeletedAtUtc).HasColumnType("timestamp with time zone");
        callHistory.HasOne(x => x.User)
            .WithMany(x => x.CallHistory)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        callHistory.HasIndex(x => new { x.UserId, x.IsDeleted, x.CreatedAtUtc });
        callHistory.HasIndex(x => x.DeletedAtUtc);

        var callSession = modelBuilder.Entity<CallSession>();
        callSession.ToTable("call_sessions");
        callSession.HasKey(x => x.Id);
        callSession.Property(x => x.Provider).IsRequired().HasMaxLength(40);
        callSession.Property(x => x.ProviderRoomId).HasMaxLength(200);
        callSession.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        callSession.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        callSession.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
        callSession.Property(x => x.EndedAt).HasColumnType("timestamp with time zone");
        callSession.HasOne(x => x.CreatedByUser)
            .WithMany(x => x.CreatedCallSessions)
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        callSession.HasIndex(x => x.CalleeUserId);

        var callParticipant = modelBuilder.Entity<CallParticipant>();
        callParticipant.ToTable("call_participants");
        callParticipant.HasKey(x => x.Id);
        callParticipant.Property(x => x.JoinedAt).HasColumnType("timestamp with time zone");
        callParticipant.Property(x => x.LeftAt).HasColumnType("timestamp with time zone");
        callParticipant.Property(x => x.BilledSeconds).HasDefaultValue(0);
        callParticipant.HasOne(x => x.CallSession)
            .WithMany(x => x.Participants)
            .HasForeignKey(x => x.CallSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        callParticipant.HasOne(x => x.User)
            .WithMany(x => x.CallParticipants)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        callParticipant.HasIndex(x => new { x.CallSessionId, x.UserId }).IsUnique();

        var monthlyUsage = modelBuilder.Entity<MonthlyUsage>();
        monthlyUsage.ToTable("monthly_usage");
        monthlyUsage.HasKey(x => new { x.UserId, x.MonthYYYYMM });
        monthlyUsage.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        monthlyUsage.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var pushToken = modelBuilder.Entity<PushToken>();
        pushToken.ToTable("push_tokens");
        pushToken.HasKey(x => x.Id);
        pushToken.Property(x => x.Token).IsRequired().HasMaxLength(512);
        pushToken.Property(x => x.DeviceId).HasMaxLength(128);
        pushToken.Property(x => x.Platform).IsRequired().HasMaxLength(16);
        pushToken.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        pushToken.Property(x => x.LastSeenAt).HasColumnType("timestamp with time zone");
        pushToken.HasOne(x => x.User)
            .WithMany(x => x.PushTokens)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        pushToken.HasIndex(x => new { x.UserId, x.Token }).IsUnique();

        var oneTimeInviteCode = modelBuilder.Entity<OneTimeInviteCode>();
        oneTimeInviteCode.ToTable("one_time_invite_codes");
        oneTimeInviteCode.HasKey(x => x.Id);
        oneTimeInviteCode.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);//.IsFixedLength();
        oneTimeInviteCode.Property(x => x.ExpiresAtUtc).HasColumnType("timestamp with time zone");
        oneTimeInviteCode.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        oneTimeInviteCode.Property(x => x.UsedAtUtc).HasColumnType("timestamp with time zone");
        oneTimeInviteCode.Property(x => x.RevokedAtUtc).HasColumnType("timestamp with time zone");
        oneTimeInviteCode.Property(x => x.MaxRedemptions).HasDefaultValue(1);
        oneTimeInviteCode.Property(x => x.RedemptionCount).HasDefaultValue(0);
        oneTimeInviteCode.HasOne(x => x.OwnerUser)
            .WithMany(x => x.OneTimeInviteCodes)
            .HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
        oneTimeInviteCode.HasOne(x => x.UsedByUser)
            .WithMany()
            .HasForeignKey(x => x.UsedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        oneTimeInviteCode.HasIndex(x => x.TokenHash).IsUnique();
        oneTimeInviteCode.HasIndex(x => new { x.OwnerUserId, x.ExpiresAtUtc });

        var message = modelBuilder.Entity<Message>();
        message.ToTable("messages");
        message.HasKey(x => x.Id);
        message.Property(x => x.EncryptedMessage).IsRequired().HasMaxLength(4000).HasDefaultValue(string.Empty);
        message.Property(x => x.EncryptedKey).IsRequired().HasMaxLength(1024).HasDefaultValue(string.Empty);
        message.Property(x => x.EncryptedKeyForSender).IsRequired().HasMaxLength(1024).HasDefaultValue(string.Empty);
        message.Property(x => x.Iv).IsRequired().HasMaxLength(32).HasDefaultValue(string.Empty);
        message.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        message.Property(x => x.IsDeleted).HasDefaultValue(false);
        message.Property(x => x.DeletedAtUtc).HasColumnType("timestamp with time zone");
        message.HasOne(x => x.SenderUser)
            .WithMany(x => x.SentMessages)
            .HasForeignKey(x => x.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict);
        message.HasOne(x => x.ReceiverUser)
            .WithMany(x => x.ReceivedMessages)
            .HasForeignKey(x => x.ReceiverUserId)
            .OnDelete(DeleteBehavior.Restrict);
        message.HasIndex(x => new { x.SenderUserId, x.ReceiverUserId, x.CreatedAtUtc });
        message.HasIndex(x => new { x.ReceiverUserId, x.CreatedAtUtc });

        var usageAdjustment = modelBuilder.Entity<UsageAdjustment>();
        usageAdjustment.ToTable("usage_adjustments");
        usageAdjustment.HasKey(x => x.Id);
        usageAdjustment.Property(x => x.Reason).IsRequired().HasMaxLength(200);
        usageAdjustment.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        usageAdjustment.HasOne(x => x.User)
            .WithMany(x => x.UsageAdjustments)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
