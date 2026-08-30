using System.Text;
using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;

namespace MeetScheduleServer.Endpoints;

public static class DataEndpoints
{
    public static IEndpointRouteBuilder MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        // 通过 Bot（参会者身份）SendData —— 走 Bot 的 Data Channel
        app.MapPost("/api/schedules/{id}/data", async (
            string id, SendDataRequest req, BotManager bots, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Message))
            {
                return Results.BadRequest(new { error = "message 必填" });
            }
            try
            {
                await bots.SendDataAsync(id, Encoding.UTF8.GetBytes(req.Message), new DataMessageOptions
                {
                    Reliable = req.Reliable ?? true,
                    Topic = req.Topic,
                    DestinationIdentities = req.DestinationIdentities,
                }, ct);
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[data] bot SendData 失败 id={Id}", id);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        // 服务端 SendData（无需 bot 入会，由服务器直接注入数据包）
        app.MapPost("/api/rooms/{roomName}/data", async (
            string roomName, SendDataRequest req, ILiveKitServerApi api, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Message))
            {
                return Results.BadRequest(new { error = "message 必填" });
            }
            try
            {
                await api.SendDataAsync(
                    roomName,
                    Encoding.UTF8.GetBytes(req.Message),
                    req.Reliable ?? true,
                    req.Topic,
                    req.DestinationIdentities,
                    ct);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[data] 服务端 SendData 失败 room={Room}", roomName);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        // 查询房间内参会者
        app.MapGet("/api/rooms/{roomName}/participants", async (
            string roomName, ILiveKitServerApi api, CancellationToken ct) =>
            Results.Ok(await api.ListParticipantsAsync(roomName, ct)));

        return app;
    }
}
