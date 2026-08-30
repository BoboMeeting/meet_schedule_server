using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;

namespace MeetScheduleServer.Tests;

/// <summary>LiveKit 服务端 API 假实现：记录调用，可注入故障</summary>
public sealed class FakeLiveKitServerApi : ILiveKitServerApi
{
    public List<string> CreatedRooms { get; } = new();
    public List<string> DeletedRooms { get; } = new();
    public List<(string Room, byte[] Data, bool Reliable, string? Topic, IReadOnlyList<string>? Dest)> SentData { get; } = new();
    public Exception? CreateRoomException { get; set; }

    public Task CreateRoomAsync(string roomName, int emptyTimeoutSeconds = 300, CancellationToken ct = default)
    {
        if (CreateRoomException is not null)
        {
            throw CreateRoomException;
        }
        CreatedRooms.Add(roomName);
        return Task.CompletedTask;
    }

    public Task DeleteRoomAsync(string roomName, CancellationToken ct = default)
    {
        DeletedRooms.Add(roomName);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ParticipantInfo>> ListParticipantsAsync(string roomName, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ParticipantInfo>>(new List<ParticipantInfo>());
    }

    public Task SendDataAsync(string roomName, byte[] data, bool reliable = true, string? topic = null,
        IReadOnlyList<string>? destinationIdentities = null, CancellationToken ct = default)
    {
        SentData.Add((roomName, data, reliable, topic, destinationIdentities));
        return Task.CompletedTask;
    }
}

/// <summary>Bot 驱动假实现：记录调用，可注入故障</summary>
public sealed class FakeBotDriver : IBotDriver
{
    public List<BotHandle> Joined { get; } = new();
    public List<BotHandle> Left { get; } = new();
    public List<(BotHandle Handle, byte[] Data, DataMessageOptions Options)> Sent { get; } = new();
    public Exception? JoinException { get; set; }

    public Task<BotHandle> JoinAsync(BotJoinRequest request, CancellationToken ct = default)
    {
        if (JoinException is not null)
        {
            throw JoinException;
        }
        var handle = new BotHandle(request.Identity, request.RoomName);
        Joined.Add(handle);
        return Task.FromResult(handle);
    }

    public Task SendDataAsync(BotHandle handle, byte[] data, DataMessageOptions options, CancellationToken ct = default)
    {
        Sent.Add((handle, data, options));
        return Task.CompletedTask;
    }

    public Task LeaveAsync(BotHandle handle, CancellationToken ct = default)
    {
        Left.Add(handle);
        return Task.CompletedTask;
    }
}
