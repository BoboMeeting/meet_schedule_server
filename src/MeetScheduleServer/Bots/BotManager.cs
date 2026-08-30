using System.Collections.Concurrent;

namespace MeetScheduleServer.Bots;

/// <summary>
/// Bot 生命周期管理：按 scheduleId 维护 bot 实例（同一调度任务同一时间只允许一个 bot）。
/// </summary>
public sealed class BotManager
{
    private readonly IBotDriver _driver;
    private readonly ConcurrentDictionary<string, BotHandle> _bots = new(); // key: scheduleId

    public BotManager(IBotDriver driver)
    {
        _driver = driver;
    }

    public BotHandle? Get(string scheduleId) =>
        _bots.TryGetValue(scheduleId, out var handle) ? handle : null;

    public bool IsInRoom(string scheduleId) => _bots.ContainsKey(scheduleId);

    public async Task<BotHandle> JoinAsync(string scheduleId, BotJoinRequest request, CancellationToken ct = default)
    {
        if (_bots.ContainsKey(scheduleId))
        {
            throw new InvalidOperationException($"schedule {scheduleId} 的 bot 已存在");
        }
        var handle = await _driver.JoinAsync(request, ct);
        _bots[scheduleId] = handle;
        return handle;
    }

    /// <summary>通过 bot（参会者身份）发送数据（Data Channel）</summary>
    public async Task SendDataAsync(string scheduleId, byte[] data, DataMessageOptions options, CancellationToken ct = default)
    {
        if (!_bots.TryGetValue(scheduleId, out var handle))
        {
            throw new InvalidOperationException("bot 不在会中");
        }
        await _driver.SendDataAsync(handle, data, options, ct);
    }

    public async Task LeaveAsync(string scheduleId, CancellationToken ct = default)
    {
        if (_bots.TryRemove(scheduleId, out var handle))
        {
            await _driver.LeaveAsync(handle, ct);
        }
    }
}
