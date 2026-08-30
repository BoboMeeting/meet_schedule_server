using MeetScheduleServer.Models;
using MeetScheduleServer.Scheduling;

namespace MeetScheduleServer.Endpoints;

public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/schedules").WithTags("Schedules");

        // 创建会议调度（到期自动派 bot 入会；StartAt 缺省立即开始）
        group.MapPost("/", async (CreateScheduleRequest req, IScheduleStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RoomName))
            {
                return Results.BadRequest(new { error = "roomName 必填" });
            }
            var now = DateTimeOffset.UtcNow;
            var schedule = new Schedule
            {
                Id = Guid.NewGuid().ToString("N"),
                RoomName = req.RoomName,
                StartAt = req.StartAt ?? now,
                DurationSeconds = req.DurationSeconds ?? 3600,
                Bot = req.Bot,
                Status = ScheduleStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await store.AddAsync(schedule, ct);
            return Results.Created($"/api/schedules/{schedule.Id}", schedule);
        });

        group.MapGet("/", async (IScheduleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        group.MapGet("/{id}", async (string id, IScheduleStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound(new { error = "not found" }));

        // 立即开始（跳过等待时间）
        group.MapPost("/{id}/start", async (string id, IScheduleStore store, SchedulerService scheduler, CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null)
            {
                return Results.NotFound(new { error = "not found" });
            }
            try
            {
                await scheduler.StartNowAsync(id, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            return Results.Ok(await store.GetAsync(id, ct));
        });

        // 取消调度
        group.MapDelete("/{id}", async (string id, SchedulerService scheduler, CancellationToken ct) =>
        {
            await scheduler.CancelAsync(id, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
