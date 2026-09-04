using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeetScheduleServer.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MeetScheduleServer.Auth;

/// <summary>房间凭证中解析出的参会信息。</summary>
public sealed record RoomTicketClaims(
    string RoomName,
    string Identity,
    string Name,
    bool IsHost,
    string ConferenceId);

/// <summary>
/// 房间凭证（room ticket）签发/校验。
/// 管理平台调用内部接口创建房间后，调度服务签发短期 ticket；
/// 客户端凭用户 JWT + ticket 调用外部接口换取 LiveKit Token。
/// </summary>
public interface IRoomTicketService
{
    /// <summary>签发房间凭证（aud=RoomTicket，5 分钟有效）。</summary>
    string Issue(string roomName, string identity, string name, bool isHost, string? conferenceId);

    /// <summary>校验凭证签名/受众/有效期并解析 claims；任何失败返回 false。</summary>
    bool TryValidate(string ticket, out RoomTicketClaims claims);
}

public sealed class RoomTicketService : IRoomTicketService
{
    /// <summary>ticket 有效期：客户端拿到后立即换取 LiveKit Token，5 分钟足够。</summary>
    private static readonly TimeSpan TicketTtl = TimeSpan.FromMinutes(5);

    private readonly AuthOptions _auth;

    public RoomTicketService(IOptions<AuthOptions> auth) => _auth = auth.Value;

    public string Issue(string roomName, string identity, string name, bool isHost, string? conferenceId)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, identity),
            new Claim("room", roomName),
            new Claim("name", name),
            new Claim("host", isHost ? "true" : "false"),
            new Claim("cid", conferenceId ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: null, // ticket 为调度服务自签自验，不校验 issuer
            audience: AuthOptions.RoomTicketAudience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(TicketTtl).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_auth.JwtSecret)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool TryValidate(string ticket, out RoomTicketClaims claims)
    {
        claims = default!;
        if (string.IsNullOrWhiteSpace(ticket)) return false;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(ticket, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = true,
                ValidAudience = AuthOptions.RoomTicketAudience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_auth.JwtSecret)),
                ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);

            // sub 经 JwtSecurityTokenHandler 默认映射为 NameIdentifier，两者兼容读取
            var identity = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var room = principal.FindFirst("room")?.Value;
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(room))
                return false;

            var name = principal.FindFirst("name")?.Value ?? identity;
            var isHost = string.Equals(
                principal.FindFirst("host")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var cid = principal.FindFirst("cid")?.Value ?? string.Empty;

            claims = new RoomTicketClaims(room, identity, name, isHost, cid);
            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
