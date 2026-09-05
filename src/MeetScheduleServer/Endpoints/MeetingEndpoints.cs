using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MeetScheduleServer.Auth;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace MeetScheduleServer.Endpoints;

/// <summary>
/// 入会相关接口，按调用方分区（安全约定）：
///   - 内部（微服务间）：/api/v1/internal/*  —— 管理平台调用，JWT aud=Service，只验签名+受众
///   - 外部（客户端）：  /api/v1/external/rooms/*    —— App 调用，用户 JWT（aud=BoboMeet.Client，携带 UserID）
/// </summary>
public static class MeetingEndpoints
{
    public static IEndpointRouteBuilder MapMeetingEndpoints(this IEndpointRouteBuilder app)
    {
        // ===== 内部：管理平台 → 调度服务，创建/绑定媒体房间并签发房间凭证 =====
        app.MapPost("/api/v1/internal/rooms", async (
            InternalCreateRoomRequest req,
            ILiveKitServerApi liveKitApi,
            IRoomTicketService tickets,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RoomName) || string.IsNullOrWhiteSpace(req.Identity))
            {
                logger.LogWarning("[meeting] 内部建房失败：roomName/identity 必填，conference={ConferenceId}", req.ConferenceId);
                return Results.BadRequest(new { error = "roomName/identity 必填" });
            }

            // 幂等确保 LiveKit 媒体房间存在（同名重复创建为 no-op）
            try
            {
                await liveKitApi.CreateRoomAsync(req.RoomName, ct: ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[meeting] 创建 LiveKit 房间失败 room={Room}", req.RoomName);
                return Results.Json(
                    new { error = "媒体房间创建失败" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var ticket = tickets.Issue(
                req.RoomName,
                req.Identity,
                string.IsNullOrWhiteSpace(req.Name) ? req.Identity : req.Name,
                req.IsHost,
                req.ConferenceId);

            logger.LogInformation(
                "[meeting] 内部建房成功：room={Room}，场次={ConferenceId}，参会者={Identity}（{Name}，isHost={IsHost}）",
                req.RoomName, req.ConferenceId, req.Identity, req.Name, req.IsHost);
            return Results.Created(
                $"/api/v1/internal/rooms/{req.RoomName}",
                new RoomTicketResponse(req.RoomName, ticket, DateTimeOffset.UtcNow.AddMinutes(5)));
        }).RequireAuthorization(AuthPolicies.InternalService);

        // ===== 外部：App 凭用户 JWT + 房间凭证换取 LiveKit 连接参数 =====
        app.MapPost("/api/v1/external/rooms/join", async (
            ExternalJoinRequest req,
            HttpContext ctx,
            IRoomTicketService tickets,
            ILiveKitTokenFactory tokenFactory,
            IOptions<LiveKitOptions> liveKit,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // 用户 JWT 中的 UserID（sub 已由 JwtBearer 映射为 NameIdentifier）
            var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                         ?? ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Ticket))
            {
                logger.LogWarning("[meeting] 外部入会失败：ticket 必填，用户={UserId}", userId);
                return Results.BadRequest(new { error = "ticket 必填" });
            }

            if (!tickets.TryValidate(req.Ticket, out var ticket))
            {
                // 凭证校验失败是入会排障高频点：签名无效/过期/受众不符统一在此暴露
                logger.LogWarning("[meeting] 外部入会失败：房间凭证无效或已过期，用户={UserId}", userId);
                return Results.Unauthorized();
            }

            // 防凭证跨用户盗用：ticket 中的参会者必须与登录用户一致
            if (!string.Equals(ticket.Identity, userId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "[meeting] 外部入会失败：凭证与用户不匹配（疑似盗用），用户={UserId}，凭证身份={TicketIdentity}，房间={Room}",
                    userId, ticket.Identity, ticket.RoomName);
                return Results.Json(
                    new { error = "房间凭证与当前用户不匹配" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var liveKitToken = tokenFactory.CreateClientToken(
                ticket.RoomName, ticket.Identity, ticket.Name, ticket.IsHost);

            logger.LogInformation(
                "[meeting] 外部入会成功：room={Room}，用户={UserId}（{Name}，isHost={IsHost}），场次={ConferenceId}",
                ticket.RoomName, ticket.Identity, ticket.Name, ticket.IsHost, ticket.ConferenceId);
            return Results.Ok(new ExternalJoinResponse(
                ticket.RoomName,
                ticket.Identity,
                ticket.Name,
                ticket.IsHost,
                liveKit.Value.Url,
                liveKitToken));
        }).RequireAuthorization(AuthPolicies.ExternalUser);

        return app;
    }
}

// ==================== DTO ====================

/// <summary>内部接口请求：管理平台为某个入会用户创建房间并申请凭证。</summary>
public sealed record InternalCreateRoomRequest(
    string? RoomName,
    string? ConferenceId,
    string? Identity,
    string? Name,
    bool IsHost = false);

/// <summary>内部接口响应：房间凭证（短期 JWT）。</summary>
public sealed record RoomTicketResponse(
    string RoomName,
    string Ticket,
    DateTimeOffset ExpiresAt);

/// <summary>外部接口请求：客户端凭房间凭证换 LiveKit Token。</summary>
public sealed record ExternalJoinRequest(string? Ticket);

/// <summary>外部接口响应：LiveKit 连接参数。</summary>
public sealed record ExternalJoinResponse(
    string RoomName,
    string Identity,
    string Name,
    bool IsHost,
    string LiveKitUrl,
    string LiveKitToken);
