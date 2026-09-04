using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;

namespace MeetScheduleServer.LiveKit;

/// <summary>
/// LiveKit 客户端入会 Token 工厂（媒体层凭证）。
/// 仅调度服务持有 LiveKit ApiKey/ApiSecret 并负责签发；
/// 管理平台与客户端都不再接触 LiveKit 密钥。
/// </summary>
public interface ILiveKitTokenFactory
{
    /// <param name="roomName">LiveKit 房间名</param>
    /// <param name="identity">参会者唯一标识（管理平台用户 Id）</param>
    /// <param name="name">展示昵称</param>
    /// <param name="isHost">是否主持人</param>
    string CreateClientToken(string roomName, string identity, string name, bool isHost);
}

public sealed class LiveKitTokenFactory : ILiveKitTokenFactory
{
    private readonly LiveKitOptions _opt;

    public LiveKitTokenFactory(IOptions<LiveKitOptions> opt) => _opt = opt.Value;

    public string CreateClientToken(string roomName, string identity, string name, bool isHost)
    {
        // 使用 LiveKit 官方 SDK 签发，grants 显式包含所需权限；
        // CanUpdateOwnMetadata 必须显式授予，否则客户端 setMetadata 会被服务端 401 拒绝。
        return new AccessToken(_opt.ApiKey, _opt.ApiSecret)
            .WithIdentity(identity)
            .WithName(name)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = true,
                CanSubscribe = true,
                CanPublishData = true,
                CanUpdateOwnMetadata = true,
            })
            .WithTtl(TimeSpan.FromHours(6))
            .ToJwt();
    }
}
