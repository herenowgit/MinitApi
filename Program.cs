using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Endpoints;
using Workspace.Services;

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
builder.Services.AddSingleton<ICodeGenerator, CodeGenerator>();

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
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

var api = app.MapGroup("/api");
api.MapUserEndpoints();
api.MapContactEndpoints();
api.MapCallEndpoints();
api.MapUsageEndpoints();
api.MapAdminEndpoints();

app.Run();

public partial class Program;
