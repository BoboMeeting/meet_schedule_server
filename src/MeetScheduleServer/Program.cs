using MeetScheduleServer.Bots;
using MeetScheduleServer.Endpoints;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Options;
using MeetScheduleServer.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection(LiveKitOptions.SectionName));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection(SchedulerOptions.SectionName));

builder.Services.AddSingleton<IScheduleStore, InMemoryScheduleStore>();
builder.Services.AddSingleton<ILiveKitServerApi, LiveKitServerApi>();
builder.Services.AddSingleton<LiveKitWebhookService>();
builder.Services.AddSingleton<IBotDriver, RtcBotDriver>();
builder.Services.AddSingleton<BotManager>();
// 注册具体类型供 Minimal API 端点注入；HostedService 复用同一实例
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

// Webhook 端点读取 raw body，无需 JSON 模型绑定，先于其余路由注册（仅语义清晰，路由互不冲突）
app.MapWebhookEndpoint();
app.MapScheduleEndpoints();
app.MapDataEndpoints();

app.Run();

// 供 WebApplicationFactory<Program> 集成测试使用
public partial class Program { }
