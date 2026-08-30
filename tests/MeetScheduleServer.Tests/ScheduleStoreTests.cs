using MeetScheduleServer.Models;
using MeetScheduleServer.Scheduling;

namespace MeetScheduleServer.Tests;

public class ScheduleStoreTests
{
    private static Schedule NewSchedule(string id = "abc12345", string room = "room-1") => new()
    {
        Id = id,
        RoomName = room,
        StartAt = DateTimeOffset.UtcNow,
        DurationSeconds = 600,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Add_Get_RoundTrip()
    {
        IScheduleStore store = new InMemoryScheduleStore();
        var s = NewSchedule();
        await store.AddAsync(s);
        var got = await store.GetAsync(s.Id);
        Assert.NotNull(got);
        Assert.Equal(s.RoomName, got!.RoomName);
        Assert.Equal(ScheduleStatus.Pending, got.Status);
    }

    [Fact]
    public async Task Get_ReturnsDefensiveCopy()
    {
        IScheduleStore store = new InMemoryScheduleStore();
        await store.AddAsync(NewSchedule());
        var a = await store.GetAsync("abc12345");
        a!.Status = ScheduleStatus.Running; // 修改副本不应影响存储
        var b = await store.GetAsync("abc12345");
        Assert.Equal(ScheduleStatus.Pending, b!.Status);
    }

    [Fact]
    public async Task Update_MutatesAndReturnsCopy()
    {
        IScheduleStore store = new InMemoryScheduleStore();
        await store.AddAsync(NewSchedule());
        var updated = await store.UpdateAsync("abc12345", s =>
        {
            s.Status = ScheduleStatus.Running;
            s.BotIdentity = "bot-abc12345";
        });
        Assert.NotNull(updated);
        Assert.Equal(ScheduleStatus.Running, updated!.Status);
        Assert.Equal("bot-abc12345", updated.BotIdentity);
    }

    [Fact]
    public async Task Update_UnknownId_ReturnsNull()
    {
        IScheduleStore store = new InMemoryScheduleStore();
        var updated = await store.UpdateAsync("nope", s => s.Status = ScheduleStatus.Running);
        Assert.Null(updated);
    }

    [Fact]
    public async Task GetAll_ReturnsAll()
    {
        IScheduleStore store = new InMemoryScheduleStore();
        await store.AddAsync(NewSchedule("id-1"));
        await store.AddAsync(NewSchedule("id-2", "room-2"));
        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
    }
}
