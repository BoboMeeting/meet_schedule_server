using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;

namespace MeetScheduleServer.LiveKit;

/// <summary>
/// LiveKit Webhook 验签与解析（封装 SDK 的 WebhookReceiver）。
/// 注意：路由必须读取 raw body 传给 Receive，否则验签失败。
/// </summary>
public sealed class LiveKitWebhookService
{
    private readonly WebhookReceiver _receiver;

    public LiveKitWebhookService(IOptions<LiveKitOptions> options)
    {
        _receiver = new WebhookReceiver(options.Value.ApiKey, options.Value.ApiSecret);
    }

    /// <summary>
    /// 验签并解析 Webhook 事件；验签失败抛出异常。
    /// </summary>
    public WebhookEvent Receive(string rawBody, string? authHeader)
    {
        return _receiver.Receive(rawBody, authHeader ?? string.Empty);
    }
}
