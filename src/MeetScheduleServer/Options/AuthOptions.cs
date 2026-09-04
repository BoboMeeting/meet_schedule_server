namespace MeetScheduleServer.Options;

/// <summary>
/// 认证配置（appsettings.json "Auth" 节，可用环境变量 Auth__JwtSecret 等覆盖）。
/// 调度服务不自行签发用户 JWT，仅验证管理平台签发的 token，因此：
///   - JwtSecret 必须与管理平台 Jwt:Secret 一致（HS256 共享密钥）
///   - Issuer / ClientAudience 必须与管理平台 Jwt 配置一致
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>JWT 签名共享密钥（≥32 字节），与管理平台 Jwt:Secret 保持一致。</summary>
    public string JwtSecret { get; set; } = "dev-secret-change-me-please-32bytes-or-more";

    /// <summary>用户 JWT 签发者（管理平台）。</summary>
    public string Issuer { get; set; } = "BoboMeet.ManagerPlatform";

    /// <summary>客户端（用户）JWT 受众，外部接口校验。</summary>
    public string ClientAudience { get; set; } = "BoboMeet.Client";

    /// <summary>微服务间 JWT 受众：管理平台 → 调度服务内部接口。</summary>
    public const string ServiceAudience = "Service";

    /// <summary>房间凭证（room ticket）JWT 受众：调度服务自签自验。</summary>
    public const string RoomTicketAudience = "RoomTicket";
}
