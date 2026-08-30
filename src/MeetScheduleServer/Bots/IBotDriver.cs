namespace MeetScheduleServer.Bots;

/// <summary>已入会 bot 的句柄</summary>
public sealed record BotHandle(string Identity, string RoomName);

/// <summary>Bot 入会请求</summary>
public sealed class BotJoinRequest
{
    public required string RoomName { get; init; }

    public required string Identity { get; init; }

    public string? DisplayName { get; init; }

    public string? Metadata { get; init; }

    /// <summary>入会后自动广播的欢迎消息</summary>
    public string? Greeting { get; init; }
}

/// <summary>Bot 发送数据的选项</summary>
public sealed class DataMessageOptions
{
    /// <summary>true=RELIABLE 可靠有序；false=LOSSY 低延迟可丢，默认 true</summary>
    public bool Reliable { get; set; } = true;

    /// <summary>应用层消息主题</summary>
    public string? Topic { get; set; }

    /// <summary>指定接收者 identity；为空则广播</summary>
    public IReadOnlyList<string>? DestinationIdentities { get; set; }
}

/// <summary>
/// Bot 驱动抽象：屏蔽“bot 如何以参会者身份入会”的实现细节，便于测试替换。
/// </summary>
public interface IBotDriver
{
    Task<BotHandle> JoinAsync(BotJoinRequest request, CancellationToken ct = default);

    Task SendDataAsync(BotHandle handle, byte[] data, DataMessageOptions options, CancellationToken ct = default);

    Task LeaveAsync(BotHandle handle, CancellationToken ct = default);
}
