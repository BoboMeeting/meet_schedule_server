using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MeetScheduleServer.Bots;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace MeetScheduleServer.Tests;

/// <summary>
/// HTTP API 集成测试：用假实现替换 LiveKit 服务端 API 与 Bot 驱动，不依赖真实 LiveKit 服务器。
/// </summary>
public class EndpointsTests : IDisposable
{
    private const string TestJwtSecret = "test-secret-test-secret-test-secret-32b!";
    private const string TestIssuer = "BoboMeet.ManagerPlatform";
    private const string ClientAudience = "BoboMeet.Client";
    private const string ServiceAudience = "Service";
    private const string RoomTicketAudience = "RoomTicket";

    private readonly FakeLiveKitServerApi _api = new();
    private readonly FakeBotDriver _driver = new();
    private readonly WebApplicationFactory<Program> _factory;

    public EndpointsTests()
    {
        // Program 启动期从配置读取签名密钥；环境变量在 App 配置中优先级高于 appsettings.json，
        // 可稳定覆盖（WebApplicationFactory 的 InMemoryCollection 可能被 appsettings 压过）。
        Environment.SetEnvironmentVariable("Auth__JwtSecret", TestJwtSecret);
        Environment.SetEnvironmentVariable("Auth__Issuer", TestIssuer);
        Environment.SetEnvironmentVariable("Auth__ClientAudience", ClientAudience);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // 测试宿主使用内存 DataProtection，避免向 AppData 写密钥环
                services.AddDataProtection().UseEphemeralDataProtectionProvider();

                services.RemoveAll<ILiveKitServerApi>();
                services.AddSingleton<ILiveKitServerApi>(_api);
                services.RemoveAll<IBotDriver>();
                services.AddSingleton<IBotDriver>(_driver);
            });
        });
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>签发测试 JWT（audience 决定分区：Service=内部 / BoboMeet.Client=外部用户）。</summary>
    private static string IssueToken(string audience, string? sub, int expireSeconds = 300, string? secret = null)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? TestJwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>();
        if (sub is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, sub));
        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddSeconds(expireSeconds),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(req);
    }

    private sealed record RoomTicketReply(string RoomName, string Ticket, DateTimeOffset ExpiresAt);
    private sealed record JoinReply(string RoomName, string Identity, string Name, bool IsHost, string LiveKitUrl, string LiveKitToken);

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

    // ===== 入会流程：内部接口（管理平台 → 调度服务，aud=Service）=====

    [Fact]
    public async Task InternalCreateRoom_WithServiceToken_ReturnsTicketAndCreatesRoom()
    {
        var client = CreateClient();
        var serviceToken = IssueToken(ServiceAudience, "manager-platform");

        var resp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-join-1",
            conferenceId = "conf-1",
            identity = "user-1",
            name = "张三",
            isHost = true,
        }, serviceToken);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RoomTicketReply>();
        Assert.False(string.IsNullOrEmpty(body!.Ticket));
        Assert.Equal("room-join-1", body.RoomName);
        Assert.Contains("room-join-1", _api.CreatedRooms);
    }

    [Fact]
    public async Task InternalCreateRoom_WithoutToken_Returns401()
    {
        var client = CreateClient();
        var resp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-x", identity = "user-1",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task InternalCreateRoom_WithClientAudienceToken_Returns401()
    {
        var client = CreateClient();
        // 用户 JWT（aud=BoboMeet.Client）不得调用内部接口
        var userToken = IssueToken(ClientAudience, "user-1");

        var resp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-x", identity = "user-1",
        }, userToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task InternalCreateRoom_MissingRoomName_Returns400()
    {
        var client = CreateClient();
        var serviceToken = IssueToken(ServiceAudience, "manager-platform");

        var resp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "", identity = "user-1",
        }, serviceToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== 入会流程：外部接口（App → 调度服务，用户 JWT + 房间凭证）=====

    [Fact]
    public async Task ExternalJoin_WithUserTokenAndValidTicket_ReturnsLiveKitToken()
    {
        var client = CreateClient();

        // 1) 管理平台经内部接口创建房间，拿到 ticket
        var serviceToken = IssueToken(ServiceAudience, "manager-platform");
        var createResp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-ext-1",
            conferenceId = "conf-1",
            identity = "user-join-1",
            name = "李四",
            isHost = false,
        }, serviceToken);
        createResp.EnsureSuccessStatusCode();
        var ticket = (await createResp.Content.ReadFromJsonAsync<RoomTicketReply>())!.Ticket;

        // 2) 客户端凭用户 JWT（sub=user-join-1）+ ticket 换 LiveKit Token
        var userToken = IssueToken(ClientAudience, "user-join-1");
        var joinResp = await PostJsonAsync(client, "/api/v1/external/rooms/join", new { ticket }, userToken);

        Assert.Equal(HttpStatusCode.OK, joinResp.StatusCode);
        var join = await joinResp.Content.ReadFromJsonAsync<JoinReply>();
        Assert.Equal("room-ext-1", join!.RoomName);
        Assert.Equal("user-join-1", join.Identity);
        Assert.False(string.IsNullOrEmpty(join.LiveKitUrl));
        Assert.False(string.IsNullOrEmpty(join.LiveKitToken));
    }

    [Fact]
    public async Task ExternalJoin_WithoutUserToken_Returns401()
    {
        var client = CreateClient();
        var serviceToken = IssueToken(ServiceAudience, "manager-platform");
        var createResp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-ext-2", identity = "user-2",
        }, serviceToken);
        var ticket = (await createResp.Content.ReadFromJsonAsync<RoomTicketReply>())!.Ticket;

        var joinResp = await PostJsonAsync(client, "/api/v1/external/rooms/join", new { ticket });
        Assert.Equal(HttpStatusCode.Unauthorized, joinResp.StatusCode);
    }

    [Fact]
    public async Task ExternalJoin_WithForgedTicket_Returns401()
    {
        var client = CreateClient();
        // 用错误密钥伪造 ticket（aud/room/sub 伪装正确）
        var forged = IssueToken(RoomTicketAudience, "user-3", secret: "another-secret-another-secret-another-32b!");
        var userToken = IssueToken(ClientAudience, "user-3");

        var joinResp = await PostJsonAsync(client, "/api/v1/external/rooms/join", new { ticket = forged }, userToken);
        Assert.Equal(HttpStatusCode.Unauthorized, joinResp.StatusCode);
    }

    [Fact]
    public async Task ExternalJoin_WithTicketOfAnotherUser_Returns403()
    {
        var client = CreateClient();

        // ticket 签发给 user-a
        var serviceToken = IssueToken(ServiceAudience, "manager-platform");
        var createResp = await PostJsonAsync(client, "/api/v1/internal/rooms", new
        {
            roomName = "room-ext-4", identity = "user-a", name = "用户A",
        }, serviceToken);
        var ticket = (await createResp.Content.ReadFromJsonAsync<RoomTicketReply>())!.Ticket;

        // 持票人却是 user-b
        var userToken = IssueToken(ClientAudience, "user-b");
        var joinResp = await PostJsonAsync(client, "/api/v1/external/rooms/join", new { ticket }, userToken);
        Assert.Equal(HttpStatusCode.Forbidden, joinResp.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
