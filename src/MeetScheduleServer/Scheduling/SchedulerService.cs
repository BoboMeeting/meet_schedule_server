using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;

namespace MeetScheduleServer.Scheduling;

/// <summary>
/// 定时调度器（骨架版：单实例内存轮询）。
/// - 到期任务：创建房间 + bot 入会，状态 Pending → Running
/// - 时长到期：bot 离会，状态 Running → Finished
/// - Webhook room_finished / 手动取消：联动清理
/// 生产建议：持久化存储 + 多实例部署时用分布式锁或数据库轮询替代进程内轮询。
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly IScheduleStore _store;
    private readonly ILiveKitServerApi _api;
    private readonly BotManager _bots;
    private readonly SchedulerOptions _options;
    private readonly ILogger<SchedulerService> _logger;
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    public SchedulerService(
        IScheduleStore store,
        ILiveKitServerApi api,
        BotManager bots,
        IOptions<SchedulerOptions> options,
        ILogger<SchedulerService> logger)
    {
        _store = store;
        _api = api;
        _bots = bots;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>后台轮询循环</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[scheduler] 已启动，轮询间隔 {Seconds}s", _options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[scheduler] tick 异常");
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>
    /// 单次调度 tick：启动到期任务、结束超时任务。
    /// 设为公开方法便于单元测试直接驱动（无需真实等待轮询）。
    /// </summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _tickLock.WaitAsync(ct);
        try
        {
            var all = await _store.GetAllAsync(ct);

            foreach (var s in all
                         .Where(s => s.Status == ScheduleStatus.Pending && s.StartAt <= now)
                         .ToList())
            {
                await StartAsync(s, now, ct);
            }

            foreach (var s in all
                         .Where(s => s.Status == ScheduleStatus.Running
                                     && s.StartedAt.HasValue
                                     && s.StartedAt.Value.AddSeconds(s.DurationSeconds) <= now)
                         .ToList())
            {
                await FinishAsync(s.Id, ScheduleStatus.Finished, ct);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>立即启动一个 Pending 任务</summary>
    public async Task StartNowAsync(string scheduleId, CancellationToken ct = default)
    {
        var s = await _store.GetAsync(scheduleId, ct)
                ?? throw new KeyNotFoundException($"schedule {scheduleId} 不存在");
        if (s.Status != ScheduleStatus.Pending)
        {
            throw new InvalidOperationException($"当前状态 {s.Status} 不可启动");
        }
        await StartAsync(s, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>取消任务（Pending 或 Running 均可）</summary>
    public async Task CancelAsync(string scheduleId, CancellationToken ct = default)
    {
        await FinishAsync(scheduleId, ScheduleStatus.Cancelled, ct);
    }

    /// <summary>Webhook 回调：房间关闭时结束对应 Running 任务</summary>
    public async Task OnRoomFinishedAsync(string roomName, CancellationToken ct = default)
    {
        var all = await _store.GetAllAsync(ct);
        foreach (var s in all
                     .Where(s => s.RoomName == roomName && s.Status == ScheduleStatus.Running)
                     .ToList())
        {
            await FinishAsync(s.Id, ScheduleStatus.Finished, ct);
        }
    }

    private async Task StartAsync(Schedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        _logger.LogInformation("[scheduler] 启动会议 id={Id} room={Room}", schedule.Id, schedule.RoomName);
        try
        {
            await _api.CreateRoomAsync(schedule.RoomName, ct: ct);
            var identity = $"{schedule.Bot?.IdentityPrefix ?? _options.BotIdentityPrefix}-{schedule.Id[..8]}".ToLowerInvariant();
            var handle = await _bots.JoinAsync(schedule.Id, new BotJoinRequest
            {
                RoomName = schedule.RoomName,
                Identity = identity,
                DisplayName = schedule.Bot?.DisplayName ?? "Meeting Bot",
                Greeting = schedule.Bot?.Greeting,
            }, ct);
            await _store.UpdateAsync(schedule.Id, s =>
            {
                s.Status = ScheduleStatus.Running;
                s.StartedAt = now;
                s.BotIdentity = handle.Identity;
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[scheduler] 启动会议失败 id={Id}", schedule.Id);
            // 清理半启动状态（bot 可能已入会）
            await _bots.LeaveAsync(schedule.Id, CancellationToken.None);
            await _store.UpdateAsync(schedule.Id, s => s.Status = ScheduleStatus.Failed, CancellationToken.None);
        }
    }

    private async Task FinishAsync(string scheduleId, ScheduleStatus status, CancellationToken ct)
    {
        await _bots.LeaveAsync(scheduleId, ct);
        await _store.UpdateAsync(scheduleId, s => s.Status = status, ct);
        _logger.LogInformation("[scheduler] 任务结束 id={Id} status={Status}", scheduleId, status);
    }
}
