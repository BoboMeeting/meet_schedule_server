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
    /// <summary>创建房间（同名重复创建为幂等操作）。agentName 为空时不派发 Agent。</summary>
    Task CreateRoomAsync(string roomName, int emptyTimeoutSeconds = 300, string? agentName = null, CancellationToken ct = default);

    /// <summary>
    /// 向房间动态派发 Agent（等价于 AgentDispatchService.CreateDispatch）。
    /// 用于房间已存在时补派 Agent；agentName 为空时使用配置中的默认 Agent。
    /// </summary>
    Task DispatchAgentAsync(string roomName, string? agentName = null, string? metadata = null, CancellationToken ct = default);

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
    private readonly AgentDispatchServiceClient _dispatchClient;
    private readonly string _agentName;
    private readonly ILogger<LiveKitServerApi> _logger;

    public LiveKitServerApi(IOptions<LiveKitOptions> options, ILogger<LiveKitServerApi> logger)
    {
        var o = options.Value;
        _client = new RoomServiceClient(o.HttpUrl, o.ApiKey, o.ApiSecret);
        _dispatchClient = new AgentDispatchServiceClient(o.HttpUrl, o.ApiKey, o.ApiSecret);
        _agentName = o.AgentName;
        _logger = logger;
    }

    public async Task CreateRoomAsync(
        string roomName,
        int emptyTimeoutSeconds = 300,
        string? agentName = null,
        CancellationToken ct = default)
    {
        var request = new CreateRoomRequest
        {
            Name = roomName,
            EmptyTimeout = (uint)emptyTimeoutSeconds,
        };

        // 仅当调用方显式指定 Agent 时才在创建房间时派发；
        // 否则保持普通建房行为，不自动派发 Agent。
        if (!string.IsNullOrEmpty(agentName))
        {
            request.Agents.Add(new RoomAgentDispatch { AgentName = agentName });
        }

        await _client.CreateRoom(request);
        _logger.LogInformation(
            "[livekit] 房间已确保存在 room={Room} agent={Agent}",
            roomName,
            string.IsNullOrEmpty(agentName) ? "-" : agentName);
    }

    public async Task DispatchAgentAsync(
        string roomName,
        string? agentName = null,
        string? metadata = null,
        CancellationToken ct = default)
    {
        var request = new CreateAgentDispatchRequest
        {
            Room = roomName,
            AgentName = agentName ?? _agentName,
        };
        if (!string.IsNullOrEmpty(metadata))
        {
            request.Metadata = metadata;
        }

        await _dispatchClient.CreateDispatch(request);
        _logger.LogInformation("[livekit] Agent 动态派发已创建 room={Room} agent={Agent}", roomName, request.AgentName);
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
