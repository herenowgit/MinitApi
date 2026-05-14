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

    // Debug: log connection string shape to diagnose Npgsql format rejection
    var preview = connectionString.Length > 10
        ? $"{connectionString[..10]}... (length: {connectionString.Length})"
        : $"(length: {connectionString.Length}, too short to preview)";
    Console.WriteLine($"[DEBUG] DATABASE_URL => {preview}");

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
