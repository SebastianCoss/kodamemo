using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using KodaMemo.Shared.Models;
using KodaMemo.Web.Components;
using KodaMemo.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Optional explicit storage for restricted local hosts; keep platform defaults otherwise.
if (builder.Configuration["DataProtection:KeyDirectory"] is { Length: > 0 } keyDirectory)
{
    var protection = builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<WebMeetingStoreService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("login", limiter =>
{
    limiter.PermitLimit = 5;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(handler => handler.Run(ctx => Results.Problem("Unexpected server error.").ExecuteAsync(ctx)));
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapLocalAuthentication();
app.MapStaticAssets();

static bool IsAuthorizedRequest(HttpContext ctx, WebMeetingStoreService store)
{
    string? authHeader = ctx.Request.Headers.Authorization.FirstOrDefault()
        ?? ctx.Request.Headers["X-Sync-Token"].FirstOrDefault();
    return store.ValidateBearerToken(authHeader);
}

app.MapGet("/api/meetings", (HttpContext ctx, WebMeetingStoreService store, string? q) =>
{
    if (!IsAuthorizedRequest(ctx, store))
    {
        return Results.Json(new { error = "401 No autorizado. Se requiere token Bearer o cabecera X-Sync-Token." }, statusCode: 401);
    }
    return Results.Ok(store.SearchMeetings(q));
});

app.MapPost("/api/sync/push", (HttpContext ctx, SyncPushPayloadDto payload, WebMeetingStoreService store) =>
{
    if (!IsAuthorizedRequest(ctx, store))
    {
        return Results.Json(new { error = "401 No autorizado. Se requiere token Bearer vÃ¡lido." }, statusCode: 401);
    }
    var res = store.SyncFromPhonePayload(payload);
    return Results.Ok(res);
});

app.MapGet("/api/sync/pull", (HttpContext ctx, WebMeetingStoreService store) =>
{
    if (!IsAuthorizedRequest(ctx, store))
    {
        return Results.Json(new { error = "401 No autorizado. Se requiere token Bearer vÃ¡lido." }, statusCode: 401);
    }
    return Results.Ok(new
    {
        serverTimeUtc = DateTime.UtcNow,
        sqliteDbPath = Path.GetFileName(store.SqliteDatabasePath),
        meetings = store.SearchMeetings(null)
    });
});

app.MapGet("/api/budget", (HttpContext ctx, WebMeetingStoreService store) =>
{
    if (!IsAuthorizedRequest(ctx, store))
    {
        return Results.Json(new { error = "401 No autorizado." }, statusCode: 401);
    }
    return Results.Ok(new
    {
        monthlyLimitUsd = store.MonthlyBudgetLimitUsd,
        reconciledUsd = store.TotalReconciledUsd,
        reservedInFlightUsd = store.ActiveReservedUsd,
        entries = store.LedgerEntries
    });
});

app.MapPost("/api/meetings/{sessionUid}/edit", (string sessionUid, HttpContext ctx, MeetingWebEditRequest req, WebMeetingStoreService store) =>
{
    if (!IsAuthorizedRequest(ctx, store))
    {
        return Results.Json(new { error = "401 No autorizado." }, statusCode: 401);
    }

    var meeting = store.GetMeetingByUid(sessionUid);
    if (meeting is null)
    {
        return Results.NotFound(new { error = $"ReuniÃ³n '{sessionUid}' no encontrada en SQLite." });
    }

    if (req.SegmentNumber.HasValue && !string.IsNullOrWhiteSpace(req.NewSegmentText))
    {
        store.UpdateTranscriptSegment(meeting, req.SegmentNumber.Value, req.NewSegmentText, "Web Blazor (EdiciÃ³n Persistida)");
    }

    if (!string.IsNullOrWhiteSpace(req.NewTaskDescription))
    {
        meeting.Tasks.Add(new TaskChecklistItemDto
        {
            SessionUid = meeting.SessionUid,
            SessionTitle = meeting.Title,
            Description = req.NewTaskDescription.Trim(),
            Assignee = string.IsNullOrWhiteSpace(req.NewTaskAssignee) ? "Usuario" : req.NewTaskAssignee.Trim(),
            DueDateText = string.IsNullOrWhiteSpace(req.NewTaskDueDate) ? "No especificada" : req.NewTaskDueDate.Trim(),
            Origin = TaskOriginType.UserDictated,
            ConfirmationStatus = UserConfirmationStatus.ConfirmedByUser,
            EvidenceQuote = "Creada y confirmada desde la interfaz Web Blazor",
            TimestampReference = "EdiciÃ³n Web",
            LastModifiedByDevice = "Web Blazor (SQLite Server)",
            UpdatedAtUtc = DateTime.UtcNow
        });
        KodaMemo.Shared.Services.MindMapGenerator.BuildFromExistingSummary(meeting);
        store.SaveMeeting(meeting);
    }

    if (!string.IsNullOrWhiteSpace(req.CompleteTaskUid))
    {
        var task = meeting.Tasks.FirstOrDefault(t => t.TaskUid == req.CompleteTaskUid);
        if (task is not null)
        {
            task.IsCompleted = true;
            task.ConfirmationStatus = UserConfirmationStatus.ConfirmedByUser;
            task.LastModifiedByDevice = "Web Blazor (SQLite Server)";
            task.UpdatedAtUtc = DateTime.UtcNow;
            store.SaveMeeting(meeting);
        }
    }

    var refreshed = store.GetMeetingByUid(sessionUid)!;
    return Results.Ok(new
    {
        success = true,
        sessionUid = refreshed.SessionUid,
        title = refreshed.Title,
        activeTranscriptVersion = refreshed.ActiveTranscriptVersion,
        activeTranscriptSha256 = refreshed.ActiveTranscriptSha256,
        tasksCount = refreshed.Tasks.Count,
        completedTasksCount = refreshed.Tasks.Count(t => t.IsCompleted),
        updatedAtUtc = refreshed.UpdatedAtUtc
    });
});

app.MapGet("/api/audio/{fileName}", (string fileName, HttpContext ctx, WebMeetingStoreService store) =>
{
    if (!(ctx.User.Identity?.IsAuthenticated == true || IsAuthorizedRequest(ctx, store)))
        return Results.Unauthorized();
    if (fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    string path = Path.Combine(Path.GetDirectoryName(store.SqliteDatabasePath)!, "audio", fileName);
    string? resolved = File.Exists(path) ? path : null;

    if (resolved is null)
    {
        return Results.NotFound(new { error = $"Archivo WAV '{fileName}' aÃºn no sincronizado bajo demanda al servidor." });
    }

    return Results.File(resolved, contentType: "audio/wav", enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

public record MeetingWebEditRequest(
    int? SegmentNumber,
    string? NewSegmentText,
    string? NewTaskDescription,
    string? NewTaskAssignee,
    string? NewTaskDueDate,
    string? CompleteTaskUid);
