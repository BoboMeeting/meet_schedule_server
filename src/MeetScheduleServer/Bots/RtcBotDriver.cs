using System.Collections.Concurrent;
using System.Text;
using LiveKit.Rtc;
using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;
using Room = LiveKit.Rtc.Room;

namespace MeetScheduleServer.Bots;

/// <summary>
/// 基于 Livekit.Rtc.Dotnet 的真实 Bot 驱动：
/// Bot 以真实参会者身份（WebRTC 客户端）入会，监听房间事件、收发 Data Channel 数据。
/// </summary>
public sealed class RtcBotDriver : IBotDriver, IDisposable
{
    private readonly LiveKitOptions _options;
    private readonly ILogger<RtcBotDriver> _logger;
    private readonly ConcurrentDictionary<string, Room> _rooms = new(); // key: bot identity

    public RtcBotDriver(IOptions<LiveKitOptions> options, ILogger<RtcBotDriver> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BotHandle> JoinAsync(BotJoinRequest request, CancellationToken ct = default)
    {
        var token = new AccessToken(_options.ApiKey, _options.ApiSecret)
            .WithIdentity(request.Identity)
            .WithName(request.DisplayName ?? "Meeting Bot")
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = request.RoomName,
                CanPublish = true,
                CanSubscribe = true,
                CanPublishData = true,
            })
            .WithTtl(TimeSpan.FromHours(6))
            .ToJwt();

        var room = new Room();
        room.ParticipantConnected += (_, p) =>
            _logger.LogInformation("[bot:{Identity}] 参会者加入 {Participant}", request.Identity, p.Identity);
        room.ParticipantDisconnected += (_, p) =>
            _logger.LogInformation("[bot:{Identity}] 参会者离开 {Participant}", request.Identity, p.Identity);
        room.DataReceived += (_, e) =>
            _logger.LogInformation(
                "[bot:{Identity}] 收到数据 from={From} topic={Topic} content={Content}",
                request.Identity,
                e.Participant?.Identity ?? "server",
                e.Topic ?? string.Empty,
                Encoding.UTF8.GetString(e.Data));
        room.Disconnected += (_, _) =>
        {
            _logger.LogInformation("[bot:{Identity}] 已离会", request.Identity);
            _rooms.TryRemove(request.Identity, out _);
        };

        await room.ConnectAsync(_options.RtcUrl, token, new RoomOptions { AutoSubscribe = true });
        _rooms[request.Identity] = room;
        _logger.LogInformation("[bot:{Identity}] 已入会 room={Room}", request.Identity, request.RoomName);

        // 入会问候：广播给当前已在房间的人（后加入者收不到，业务需要可在 participant_joined 事件里补发）
        if (!string.IsNullOrEmpty(request.Greeting) && room.LocalParticipant is { } lp)
        {
            await lp.PublishDataAsync(
                Encoding.UTF8.GetBytes(request.Greeting),
                new DataPublishOptions { Reliable = true, Topic = "bot.greeting" });
        }

        return new BotHandle(request.Identity, request.RoomName);
    }

    public async Task SendDataAsync(BotHandle handle, byte[] data, DataMessageOptions options, CancellationToken ct = default)
    {
        if (!_rooms.TryGetValue(handle.Identity, out var room) || !room.IsConnected)
        {
            throw new InvalidOperationException($"bot {handle.Identity} 未入会");
        }
        var lp = room.LocalParticipant ?? throw new InvalidOperationException($"bot {handle.Identity} 未入会");

        var publishOptions = new DataPublishOptions
        {
            Reliable = options.Reliable,
            Topic = options.Topic,
        };
        if (options.DestinationIdentities is { Count: > 0 })
        {
            publishOptions.DestinationIdentities = options.DestinationIdentities.ToArray();
        }
        await lp.PublishDataAsync(data, publishOptions);
    }

    public async Task LeaveAsync(BotHandle handle, CancellationToken ct = default)
    {
        if (_rooms.TryRemove(handle.Identity, out var room))
        {
            await room.DisconnectAsync();
            room.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var room in _rooms.Values)
        {
            room.Dispose();
        }
        _rooms.Clear();
    }
}
