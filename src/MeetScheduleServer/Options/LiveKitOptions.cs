namespace MeetScheduleServer.Options;

/// <summary>
/// LiveKit 连接配置（appsettings.json "LiveKit" 节，可用环境变量 LiveKit__Url 等覆盖）。
/// 生产环境建议用 user-secrets / 环境变量注入 ApiSecret。
/// </summary>
public sealed class LiveKitOptions
{
    public const string SectionName = "LiveKit";

    /// <summary>LiveKit 服务器地址（ws:// 或 wss://）</summary>
    public string Url { get; set; } = "ws://localhost:7880";

    public string ApiKey { get; set; } = "devkey";

    public string ApiSecret { get; set; } = "secret";

    /// <summary>Twirp HTTP 地址（服务端 API 用，ws→http / wss→https）</summary>
    public string HttpUrl =>
        Url.Replace("ws://", "http://").Replace("wss://", "https://");

    /// <summary>RTC 连接地址（Bot 入会用，保持 ws/wss）</summary>
    public string RtcUrl => Url;
}
