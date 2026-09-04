namespace MeetScheduleServer.Auth;

/// <summary>JwtBearer 认证方案：按调用方分区。</summary>
public static class AuthSchemes
{
    /// <summary>微服务间（管理平台 → 调度服务），只验签名 + 受众 aud=Service。</summary>
    public const string InternalBearer = "InternalBearer";

    /// <summary>客户端（App → 调度服务），验用户 JWT（签名/iss/aud/有效期）。</summary>
    public const string ExternalBearer = "ExternalBearer";
}

/// <summary>授权策略常量。</summary>
public static class AuthPolicies
{
    /// <summary>内部接口策略：仅接受 aud=Service 的服务 JWT。</summary>
    public const string InternalService = "InternalService";

    /// <summary>外部接口策略：仅接受用户 JWT（aud=BoboMeet.Client）。</summary>
    public const string ExternalUser = "ExternalUser";
}
