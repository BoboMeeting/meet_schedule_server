using System.Net;
using System.Net.Http.Json;
using System.Text;
using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetScheduleServer.Tests;

/// <summary>
/// HTTP API 集成测试：用假实现替换 LiveKit 服务端 API 与 Bot 驱动，不依赖真实 LiveKit 服务器。
/// </summary>
public class EndpointsTests : IDisposable
{
    private readonly FakeLiveKitServerApi _api = new();
    private readonly FakeBotDriver _driver = new();
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILiveKitServerApi>();
                services.AddSingleton<ILiveKitServerApi>(_api);
                services.RemoveAll<IBotDriver>();
                services.AddSingleton<IBotDriver>(_driver);
            });
        });
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private async Task<Schedule> CreateScheduleAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/schedules", body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Schedule>())!;
    }

    [Fact]
    public async Task CreateSchedule_WithoutStartAt_IsPendingAndFutureStartAllowed()
    {
        var client = CreateClient();

        var schedule = await CreateScheduleAsync(client, new
        {
            roomName = "room-a",
            startAt = DateTimeOffset.UtcNow.AddHours(1),
            durationSeconds = 1800,
        });

        Assert.Equal("room-a", schedule.RoomName);
        Assert.Equal(ScheduleStatus.Pending, schedule.Status);

        var list = await client.GetFromJsonAsync<List<Schedule>>("/api/schedules");
        Assert.Contains(list!, s => s.Id == schedule.Id);
    }

    [Fact]
    public async Task CreateSchedule_MissingRoomName_Returns400()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/schedules", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetUnknownSchedule_Returns404()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/api/schedules/not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task StartNow_StartsMeeting_BotJoinsAndDataSends()
    {
        var client = CreateClient();
        var schedule = await CreateScheduleAsync(client, new
        {
            roomName = "room-live",
            startAt = DateTimeOffset.UtcNow.AddHours(1), // 避免后台轮询抢先启动
        });

        var resp = await client.PostAsync($"/api/schedules/{schedule.Id}/start", null);
        resp.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<Schedule>($"/api/schedules/{schedule.Id}");
        Assert.Equal(ScheduleStatus.Running, after!.Status);
        Assert.Contains("room-live", _api.CreatedRooms);
        Assert.Single(_driver.Joined);

        // Bot SendData（bot 已入会）
        var dataResp = await client.PostAsJsonAsync($"/api/schedules/{schedule.Id}/data", new { message = "hello", topic = "t1" });
        Assert.Equal(HttpStatusCode.OK, dataResp.StatusCode);
        Assert.Single(_driver.Sent);
        Assert.Equal("hello"u8.ToArray(), _driver.Sent[0].Data);
        Assert.Equal("t1", _driver.Sent[0].Options.Topic);
    }

    [Fact]
    public async Task BotSendData_WithoutBotInRoom_Returns404()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/schedules/not-exist/data", new { message = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ServerSendData_ReachesLiveKitApi()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/rooms/room-x/data", new
        {
            message = "broadcast",
            topic = "chat",
            destinationIdentities = new[] { "user-1" },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var sent = Assert.Single(_api.SentData);
        Assert.Equal("room-x", sent.Room);
        Assert.Equal("broadcast", Encoding.UTF8.GetString(sent.Data));
        Assert.Equal("chat", sent.Topic);
        Assert.Equal("user-1", Assert.Single(sent.Dest!));
    }

    [Fact]
    public async Task CancelSchedule_ReturnsOk()
    {
        var client = CreateClient();
        var schedule = await CreateScheduleAsync(client, new { roomName = "room-c", startAt = DateTimeOffset.UtcNow.AddHours(1) });

        var resp = await client.DeleteAsync($"/api/schedules/{schedule.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await client.GetFromJsonAsync<Schedule>($"/api/schedules/{schedule.Id}");
        Assert.Equal(ScheduleStatus.Cancelled, after!.Status);
    }

    [Fact]
    public async Task Webhook_WithoutValidSignature_Returns401()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/webhook/livekit",
            new StringContent("{}", Encoding.UTF8, "application/webhook+json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
