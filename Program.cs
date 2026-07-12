using System.Threading.RateLimiting;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Endpoints;
using Workspace.Services;
using Workspace.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// ✅ Railway provides PORT. Bind to it on all interfaces.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddValidation();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (builder.Environment.IsEnvironment("Testing"))
    {
        return;
    }

    var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("Missing DATABASE_URL environment variable");

    // Manually parse postgresql:// or postgres:// URI into a key=value connection string
    if (connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
        connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[DB] Raw DATABASE_URL prefix: {connectionString[..Math.Min(20, connectionString.Length)]}...");

        var uri = new Uri(connectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var host = uri.Host;
        var dbPort = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');

        connectionString = $"Host={host};Port={dbPort};Username={user};Password={password};Database={database}";
        Console.WriteLine($"[DB] Built connection string prefix: {connectionString[..Math.Min(30, connectionString.Length)]}...");
    }

    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<QuotaService>();
builder.Services.AddScoped<PushService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddSingleton<ICodeGenerator, CodeGenerator>();
builder.Services.AddSingleton<SignalingRoomManager>();

builder.Services.AddHttpClient(TurnService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<ITurnService, TurnService>();

builder.Services.AddHostedService<CallTimeoutService>();
builder.Services.AddHostedService<CallHistoryCleanupWorker>();
builder.Services.AddHostedService<MessageCleanupWorker>();
builder.Services.AddHostedService<AccountInactivityCleanupWorker>();

// Firebase Admin — required for FCM push to callee devices.
// Credential is read from env var FIREBASE_SERVICE_ACCOUNT_JSON (Railway-friendly).
// If unset, FCM pushes are skipped (PushService logs a warning).
var firebaseCredentialJson = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON");
if (!string.IsNullOrWhiteSpace(firebaseCredentialJson) && FirebaseApp.DefaultInstance is null)
{
    try
    {
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromJson(firebaseCredentialJson)
        });
        Console.WriteLine("[FCM] FirebaseApp initialised");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FCM] Failed to initialise FirebaseApp: {ex.Message}");
    }
}
else if (string.IsNullOrWhiteSpace(firebaseCredentialJson))
{
    Console.WriteLine("[FCM] FIREBASE_SERVICE_ACCOUNT_JSON not set — incoming-call pushes will be skipped");
}

// Under the Testing environment the whole suite shares one rate-limit partition
// ("unknown-ip"), so real per-minute limits would throttle unrelated tests. Use
// effectively-unbounded limits there; production keeps the real values.
var isTestingEnv = builder.Environment.IsEnvironment("Testing");
var publicPermitLimit = isTestingEnv ? int.MaxValue : 30;
var invitePermitLimit = isTestingEnv ? int.MaxValue : 10;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static (context, _) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        var task = context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Rate limit exceeded for this endpoint.",
            Type = "https://httpstatuses.com/429"
        });

        return new ValueTask(task);
    };

    options.AddPolicy("public-per-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = publicPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("invites-per-user-or-ip", context =>
    {
        var userKey = context.Request.Headers.TryGetValue("X-User-Id", out var userId)
            ? userId.ToString()
            : null;
        var partitionKey = !string.IsNullOrWhiteSpace(userKey)
            ? $"user:{userKey}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip"}";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: partitionKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = invitePermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseExceptionHandler();
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    if (response.StatusCode < 400 || response.HasStarted)
    {
        return;
    }

    response.ContentType = "application/problem+json";
    await response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = response.StatusCode,
        Title = "Request failed",
        Detail = $"Request failed with status code {response.StatusCode}.",
        Type = $"https://httpstatuses.com/{response.StatusCode}"
    });
});
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new { service = "call-usage-api", utcNow = DateTime.UtcNow }));

// ── WebRTC Signaling ──────────────────────────────────────────────────────
// Peers connect here to exchange SDP offers/answers and ICE candidates.
// Query params: ?callId=<guid>&userId=<guid>
app.MapGet("/ws", async (
    HttpContext context,
    SignalingRoomManager roomManager,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsync("WebSocket connection required.", ct);
        return;
    }

    await SignalingHandler.HandleAsync(context, roomManager, logger, ct);
})
.WithTags("Signaling")
.WithSummary("WebRTC signaling relay — connect with ?callId=<guid>&userId=<guid>");

var api = app.MapGroup("/api");
api.MapUserEndpoints();
api.MapContactEndpoints();
api.MapCallEndpoints();
api.MapUsageEndpoints();
api.MapAdminEndpoints();
api.MapTurnEndpoints();
api.MapInviteEndpoints();
api.MapMessageEndpoints();
api.MapMessageEndpoints();

app.Run();

public partial class Program;