using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;
using MeetScheduleServer.Options;
using MeetScheduleServer.Scheduling;

namespace MeetScheduleServer.Tests;

public class SchedulerServiceTests
{
    private readonly FakeLiveKitServerApi _api = new();
    private readonly FakeBotDriver _driver = new();
    private readonly InMemoryScheduleStore _store = new();
    private readonly BotManager _bots;
    private readonly SchedulerService _scheduler;

    public SchedulerServiceTests()
    {
        _bots = new BotManager(_driver);
        _scheduler = new SchedulerService(
            _store,
            _api,
            _bots,
            Microsoft.Extensions.Options.Options.Create(new SchedulerOptions()),
            NullLogger<SchedulerService>.Instance);
    }

    private async Task<Schedule> AddScheduleAsync(Action<Schedule>? customize = null)
    {
        var now = DateTimeOffset.UtcNow;
        var s = new Schedule
        {
            Id = Guid.NewGuid().ToString("N"),
            RoomName = "room-" + Guid.NewGuid().ToString("N")[..6],
            StartAt = now,
            DurationSeconds = 3600,
            CreatedAt = now,
            UpdatedAt = now,
        };
        customize?.Invoke(s);
        await _store.AddAsync(s);
        return s;
    }

    [Fact]
    public async Task DuePending_StartsMeetingAndBotJoins()
    {
        var s = await AddScheduleAsync(x => x.StartAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        await _scheduler.TickAsync(DateTimeOffset.UtcNow);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Running, after!.Status);
        Assert.NotNull(after.StartedAt);
        Assert.NotNull(after.BotIdentity);
        Assert.Contains(s.RoomName, _api.CreatedRooms);
        var joined = Assert.Single(_driver.Joined);
        Assert.Equal(after.BotIdentity, joined.Identity);
        Assert.Equal(s.RoomName, joined.RoomName);
        // 默认 identity 前缀 bot-
        Assert.StartsWith("bot-", joined.Identity);
    }

    [Fact]
    public async Task NotDuePending_StaysPending()
    {
        var s = await AddScheduleAsync(x => x.StartAt = DateTimeOffset.UtcNow.AddHours(1));

        await _scheduler.TickAsync(DateTimeOffset.UtcNow);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Pending, after!.Status);
        Assert.Empty(_driver.Joined);
        Assert.Empty(_api.CreatedRooms);
    }

    [Fact]
    public async Task ExpiredRunning_FinishesAndBotLeaves()
    {
        var s = await AddScheduleAsync(x =>
        {
            x.Status = ScheduleStatus.Running;
            x.StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1800);
            x.DurationSeconds = 600; // 已超时
        });
        // 预置 bot 在会（与调度器共享同一 BotManager）
        await _bots.JoinAsync(s.Id, new BotJoinRequest
        {
            RoomName = s.RoomName,
            Identity = "bot-x",
        });

        await _scheduler.TickAsync(DateTimeOffset.UtcNow);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Finished, after!.Status);
        Assert.Contains(_driver.Left, h => h.Identity == "bot-x");
    }

    [Fact]
    public async Task StartFailure_MarksFailedAndCleansBot()
    {
        var s = await AddScheduleAsync(x => x.StartAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        _api.CreateRoomException = new HttpRequestException("livekit down");

        await _scheduler.TickAsync(DateTimeOffset.UtcNow);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Failed, after!.Status);
        Assert.Empty(_driver.Joined);
    }

    [Fact]
    public async Task Cancel_RunningTask_LeavesBotAndMarksCancelled()
    {
        var s = await AddScheduleAsync(x => x.StartAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        await _scheduler.TickAsync(DateTimeOffset.UtcNow);
        Assert.Equal(ScheduleStatus.Running, (await _store.GetAsync(s.Id))!.Status);

        await _scheduler.CancelAsync(s.Id);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Cancelled, after!.Status);
        Assert.Single(_driver.Left);
    }

    [Fact]
    public async Task OnRoomFinished_EndsRunningSchedulesForRoom()
    {
        var s = await AddScheduleAsync(x =>
        {
            x.Status = ScheduleStatus.Running;
            x.StartedAt = DateTimeOffset.UtcNow;
        });

        await _scheduler.OnRoomFinishedAsync(s.RoomName);

        var after = await _store.GetAsync(s.Id);
        Assert.Equal(ScheduleStatus.Finished, after!.Status);
    }
}
