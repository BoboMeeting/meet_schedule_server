namespace MeetScheduleServer.Models;

public enum ScheduleStatus
{
    /// <summary>等待开始</summary>
    Pending,
    /// <summary>会议进行中（bot 在会）</summary>
    Running,
    /// <summary>正常结束</summary>
    Finished,
    /// <summary>已取消</summary>
    Cancelled,
    /// <summary>启动失败</summary>
    Failed,
}

/// <summary>会议调度任务</summary>
public sealed class Schedule
{
    public string Id { get; set; } = string.Empty;

    /// <summary>LiveKit 房间名</summary>
    public string RoomName { get; set; } = string.Empty;

    /// <summary>开始时间，到期后自动派 bot 入会；缺省表示立即开始</summary>
    public DateTimeOffset StartAt { get; set; }

    /// <summary>会议时长（秒），到期后 bot 自动离会</summary>
    public int DurationSeconds { get; set; } = 3600;

    /// <summary>Bot 配置</summary>
    public BotConfig? Bot { get; set; }

    public ScheduleStatus Status { get; set; } = ScheduleStatus.Pending;

    /// <summary>实际开始时间（StartAsync 成功后写入）</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>实际入会的 bot identity</summary>
    public string? BotIdentity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Bot 配置</summary>
public sealed class BotConfig
{
    /// <summary>bot identity 前缀，默认 bot（最终 identity: 前缀-scheduleId 前 8 位）</summary>
    public string? IdentityPrefix { get; set; }

    /// <summary>显示名称，默认 Meeting Bot</summary>
    public string? DisplayName { get; set; }

    /// <summary>入会后自动广播的欢迎消息</summary>
    public string? Greeting { get; set; }
}

/// <summary>创建调度请求</summary>
public sealed class CreateScheduleRequest
{
    public string? RoomName { get; set; }

    /// <summary>ms 时间戳或 ISO 时间；缺省立即开始</summary>
    public DateTimeOffset? StartAt { get; set; }

    public int? DurationSeconds { get; set; }

    public BotConfig? Bot { get; set; }
}

/// <summary>SendData 请求（Bot 通道与服务端通道共用）</summary>
public sealed class SendDataRequest
{
    /// <summary>文本消息（UTF-8 编码发送）</summary>
    public string? Message { get; set; }

    /// <summary>应用层消息主题，便于客户端按 topic 分发</summary>
    public string? Topic { get; set; }

    /// <summary>指定接收者 identity 列表；为空则广播给房间内所有人</summary>
    public List<string>? DestinationIdentities { get; set; }

    /// <summary>true=RELIABLE 可靠有序；false=LOSSY 低延迟可丢，默认 true</summary>
    public bool? Reliable { get; set; }
}
