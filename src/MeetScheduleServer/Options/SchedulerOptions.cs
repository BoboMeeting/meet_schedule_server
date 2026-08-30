namespace MeetScheduleServer.Options;

/// <summary>
/// 调度器配置（appsettings.json "Scheduler" 节）。
/// </summary>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>轮询间隔（秒）</summary>
    public int PollIntervalSeconds { get; set; } = 1;

    /// <summary>Bot identity 前缀，默认 bot</summary>
    public string BotIdentityPrefix { get; set; } = "bot";
}
