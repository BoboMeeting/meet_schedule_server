using Livekit.Server.Sdk.Dotnet;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Scheduling;

namespace MeetScheduleServer.Endpoints;

public static class WebhookEndpoint
{
    public static IEndpointRouteBuilder MapWebhookEndpoint(this IEndpointRouteBuilder app)
    {
        // LiveKit Webhook 接收端点：
        // - 必须读取 raw body 验签（WebhookReceiver），不能经过 JSON 模型绑定
        // - LiveKit 侧配置（livekit.yaml）：webhook.urls 指向 http://<host>/webhook/livekit
        app.MapPost("/webhook/livekit", async (
            HttpRequest request, LiveKitWebhookService webhook, SchedulerService scheduler, ILogger<Program> logger) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var auth = request.Headers["Authorization"].FirstOrDefault();

            WebhookEvent evt;
            try
            {
                evt = webhook.Receive(body, auth);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[webhook] 验签失败");
                return Results.Unauthorized();
            }

            var roomName = evt.Room?.Name ?? string.Empty;
            var identity = evt.Participant?.Identity ?? string.Empty;

            switch (evt.Event)
            {
                case "room_started":
                    logger.LogInformation("[webhook] room_started room={Room}", roomName);
                    break;

                case "room_finished":
                    logger.LogInformation("[webhook] room_finished room={Room}", roomName);
                    // 房间关闭 → 结束对应调度任务并让 bot 离会
                    await scheduler.OnRoomFinishedAsync(roomName);
                    break;

                case "participant_joined":
                    logger.LogInformation("[webhook] participant_joined room={Room} identity={Identity}", roomName, identity);
                    // TODO: 业务逻辑，如记录参会人、向新人定向 SendData
                    break;

                case "participant_left":
                    logger.LogInformation("[webhook] participant_left room={Room} identity={Identity}", roomName, identity);
                    break;

                default:
                    logger.LogDebug("[webhook] 未处理事件 {Event}", evt.Event);
                    break;
            }

            return Results.Ok(new { @event = evt.Event });
        });

        return app;
    }
}
