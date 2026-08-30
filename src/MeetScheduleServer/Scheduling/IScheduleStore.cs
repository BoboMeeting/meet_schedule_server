using MeetScheduleServer.Models;

namespace MeetScheduleServer.Scheduling;

/// <summary>
/// 调度任务存储接口（骨架版为内存实现）。
/// 生产环境替换为 DB（SQLite/Postgres/Redis）时，实现此接口即可，上层逻辑不用改。
/// </summary>
public interface IScheduleStore
{
    Task AddAsync(Schedule schedule, CancellationToken ct = default);

    Task<Schedule?> GetAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Schedule>> GetAllAsync(CancellationToken ct = default);

    /// <summary>原子更新：mutate 在存储内部锁下执行</summary>
    Task<Schedule?> UpdateAsync(string id, Action<Schedule> mutate, CancellationToken ct = default);
}

/// <summary>内存存储实现（进程重启即丢失；生产请替换为持久化实现）。</summary>
public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Schedule> _schedules = new();

    public Task AddAsync(Schedule schedule, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _schedules[schedule.Id] = Clone(schedule);
        }
        return Task.CompletedTask;
    }

    public Task<Schedule?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_schedules.TryGetValue(id, out var s) ? Clone(s) : null);
        }
    }

    public Task<IReadOnlyList<Schedule>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Schedule>>(_schedules.Values.Select(Clone).ToList());
        }
    }

    public Task<Schedule?> UpdateAsync(string id, Action<Schedule> mutate, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_schedules.TryGetValue(id, out var s))
            {
                return Task.FromResult<Schedule?>(null);
            }
            mutate(s);
            s.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<Schedule?>(Clone(s));
        }
    }

    private static Schedule Clone(Schedule s) => new()
    {
        Id = s.Id,
        RoomName = s.RoomName,
        StartAt = s.StartAt,
        DurationSeconds = s.DurationSeconds,
        Bot = s.Bot is null
            ? null
            : new BotConfig
            {
                IdentityPrefix = s.Bot.IdentityPrefix,
                DisplayName = s.Bot.DisplayName,
                Greeting = s.Bot.Greeting,
            },
        Status = s.Status,
        StartedAt = s.StartedAt,
        BotIdentity = s.BotIdentity,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
