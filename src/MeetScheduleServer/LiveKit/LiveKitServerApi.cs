using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;

namespace MeetScheduleServer.LiveKit;

/// <summary>
/// LiveKit 服务端 API（Twirp）：房间管理、服务端 SendData、参会者查询。
/// 抽象出接口便于单元测试替换。
/// </summary>
public interface ILiveKitServerApi
{
    /// <summary>创建房间（同名重复创建为幂等操作）</summary>
    Task CreateRoomAsync(string roomName, int emptyTimeoutSeconds = 300, CancellationToken ct = default);

    Task DeleteRoomAsync(string roomName, CancellationToken ct = default);

    Task<IReadOnlyList<ParticipantInfo>> ListParticipantsAsync(string roomName, CancellationToken ct = default);

    /// <summary>
    /// 服务端 SendData：无需任何参会者入会，直接向房间注入数据包。
    /// </summary>
    Task SendDataAsync(
        string roomName,
        byte[] data,
        bool reliable = true,
        string? topic = null,
        IReadOnlyList<string>? destinationIdentities = null,
        CancellationToken ct = default);
}

/// <summary>
/// 基于 Livekit.Server.Sdk.Dotnet 的 RoomServiceClient 实现。
/// </summary>
public sealed class LiveKitServerApi : ILiveKitServerApi
{
    private readonly RoomServiceClient _client;
    private readonly ILogger<LiveKitServerApi> _logger;

    public LiveKitServerApi(IOptions<LiveKitOptions> options, ILogger<LiveKitServerApi> logger)
    {
        var o = options.Value;
        _client = new RoomServiceClient(o.HttpUrl, o.ApiKey, o.ApiSecret);
        _logger = logger;
    }

    public async Task CreateRoomAsync(string roomName, int emptyTimeoutSeconds = 300, CancellationToken ct = default)
    {
        await _client.CreateRoom(new CreateRoomRequest
        {
            Name = roomName,
            EmptyTimeout = (uint)emptyTimeoutSeconds,
        });
        _logger.LogInformation("[livekit] 房间已确保存在 room={Room}", roomName);
    }

    public async Task DeleteRoomAsync(string roomName, CancellationToken ct = default)
    {
        await _client.DeleteRoom(new DeleteRoomRequest { Room = roomName });
    }

    public async Task<IReadOnlyList<ParticipantInfo>> ListParticipantsAsync(
        string roomName,
        CancellationToken ct = default)
    {
        var resp = await _client.ListParticipants(new ListParticipantsRequest { Room = roomName });
        return resp.Participants.ToArray();
    }

    public async Task SendDataAsync(
        string roomName,
        byte[] data,
        bool reliable = true,
        string? topic = null,
        IReadOnlyList<string>? destinationIdentities = null,
        CancellationToken ct = default)
    {
        var request = new SendDataRequest
        {
            Room = roomName,
            Data = Google.Protobuf.ByteString.CopyFrom(data),
            Kind = reliable ? DataPacket.Types.Kind.Reliable : DataPacket.Types.Kind.Lossy,
        };
        if (!string.IsNullOrEmpty(topic))
        {
            request.Topic = topic;
        }
        if (destinationIdentities is { Count: > 0 })
        {
            request.DestinationIdentities.AddRange(destinationIdentities);
        }
        await _client.SendData(request);
    }
}
