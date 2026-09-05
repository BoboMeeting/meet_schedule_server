using MeetScheduleServer.Models;
using MeetScheduleServer.Scheduling;

namespace MeetScheduleServer.Endpoints;

public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/schedules").WithTags("Schedules");

        // 创建会议调度（到期自动派 bot 入会；StartAt 缺省立即开始）
        group.MapPost("/", async (
            CreateScheduleRequest req,
            IScheduleStore store,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RoomName))
            {
                logger.LogWarning("[schedule] 创建任务失败：roomName 必填");
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
            logger.LogInformation(
                "[schedule] 创建任务成功：id={Id}，room={Room}，startAt={StartAt}，duration={Duration}s，botPrefix={BotPrefix}",
                schedule.Id, schedule.RoomName, schedule.StartAt, schedule.DurationSeconds, schedule.Bot?.IdentityPrefix);
            return Results.Created($"/api/schedules/{schedule.Id}", schedule);
        });

        group.MapGet("/", async (IScheduleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        group.MapGet("/{id}", async (string id, IScheduleStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound(new { error = "not found" }));

        // 立即开始（跳过等待时间）
        group.MapPost("/{id}/start", async (
            string id,
            IScheduleStore store,
            SchedulerService scheduler,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (await store.GetAsync(id, ct) is null)
            {
                logger.LogWarning("[schedule] 手动启动失败：任务不存在 id={Id}", id);
                return Results.NotFound(new { error = "not found" });
            }
            try
            {
                await scheduler.StartNowAsync(id, ct);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("[schedule] 手动启动被拒：id={Id}，原因={Reason}", id, ex.Message);
                return Results.Conflict(new { error = ex.Message });
            }
            logger.LogInformation("[schedule] 手动启动成功：id={Id}", id);
            return Results.Ok(await store.GetAsync(id, ct));
        });

        // 取消调度
        group.MapDelete("/{id}", async (
            string id,
            SchedulerService scheduler,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            await scheduler.CancelAsync(id, ct);
            // 任务结束本身由 SchedulerService 记录，此处补充"手动触发"来源便于区分 webhook/到期结束
            logger.LogInformation("[schedule] 手动取消任务：id={Id}", id);
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
