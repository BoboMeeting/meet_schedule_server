using System.Text;
using MeetScheduleServer.Auth;
using MeetScheduleServer.Bots;
using MeetScheduleServer.Endpoints;
using MeetScheduleServer.LiveKit;
using MeetScheduleServer.Options;
using MeetScheduleServer.Scheduling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection(LiveKitOptions.SectionName));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection(SchedulerOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// ===== 认证：JWT 双方案（同一共享密钥，按受众分区）=====
var authOpt = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOpt.JwtSecret));

builder.Services.AddAuthentication()
    // 内部（微服务间）：只验签名 + 受众 aud=Service，不校验签发者/用户身份
    .AddJwtBearer(AuthSchemes.InternalBearer, o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = AuthOptions.ServiceAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    // 外部（客户端）：完整校验用户 JWT（签名/iss/aud/有效期）
    .AddJwtBearer(AuthSchemes.ExternalBearer, o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOpt.Issuer,
            ValidateAudience = true,
            ValidAudience = authOpt.ClientAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(AuthPolicies.InternalService, p => p
        .RequireAuthenticatedUser()
        .AddAuthenticationSchemes(AuthSchemes.InternalBearer));
    o.AddPolicy(AuthPolicies.ExternalUser, p => p
        .RequireAuthenticatedUser()
        .AddAuthenticationSchemes(AuthSchemes.ExternalBearer));
});

builder.Services.AddSingleton<IScheduleStore, InMemoryScheduleStore>();
builder.Services.AddSingleton<ILiveKitServerApi, LiveKitServerApi>();
builder.Services.AddSingleton<LiveKitWebhookService>();
builder.Services.AddSingleton<IBotDriver, RtcBotDriver>();
builder.Services.AddSingleton<BotManager>();
// 注册具体类型供 Minimal API 端点注入；HostedService 复用同一实例
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

// 入会：房间凭证签发/校验 + LiveKit 客户端 Token 工厂（仅调度服务持有 LiveKit 密钥）
builder.Services.AddSingleton<IRoomTicketService, RoomTicketService>();
builder.Services.AddSingleton<ILiveKitTokenFactory, LiveKitTokenFactory>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

// Webhook 端点读取 raw body，无需 JSON 模型绑定，先于其余路由注册（仅语义清晰，路由互不冲突）
app.MapWebhookEndpoint();
app.MapScheduleEndpoints();
app.MapDataEndpoints();
app.MapMeetingEndpoints();

app.Run();

// 供 WebApplicationFactory<Program> 集成测试使用
public partial class Program { }
