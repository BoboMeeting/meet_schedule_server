using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

// ===== HTTP 访问日志：统一记录 方法/路径/查询串/状态码/耗时/调用方 =====
// 刻意不记录请求体与响应体：内部接口含房间凭证签发、外部接口含 LiveKit Token，落日志有泄密风险；
// 业务上下文（哪个房间/凭证/调用方、失败原因）由各端点内的业务日志补充。
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("MeetScheduleServer.Http");
    var sw = Stopwatch.StartNew();
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex,
            "HTTP {Method} {Path}{Query} 未处理异常，耗时 {Elapsed}ms，调用方={Caller}",
            context.Request.Method, context.Request.Path, context.Request.QueryString,
            sw.ElapsedMilliseconds, GetCaller(context));
        throw;
    }

    sw.Stop();
    var status = context.Response.StatusCode;
    // 2xx/3xx→Information；4xx→Warning（客户端/调用方错误，排查高频）；5xx→Error；健康检查降为 Debug 降噪
    var level = status >= 500
        ? LogLevel.Error
        : status >= 400
            ? LogLevel.Warning
            : context.Request.Path.StartsWithSegments("/healthz")
                ? LogLevel.Debug
                : LogLevel.Information;
    logger.Log(level,
        "HTTP {Method} {Path}{Query} → {Status}，耗时 {Elapsed}ms，调用方={Caller}",
        context.Request.Method, context.Request.Path, context.Request.QueryString,
        status, sw.ElapsedMilliseconds, GetCaller(context));
});

app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));

// Webhook 端点读取 raw body，无需 JSON 模型绑定，先于其余路由注册（仅语义清晰，路由互不冲突）
app.MapWebhookEndpoint();
app.MapScheduleEndpoints();
app.MapDataEndpoints();
app.MapMeetingEndpoints();

app.Run();

// 从已认证请求提取调用方标识：外部用户 JWT → 用户 ID（NameIdentifier/sub）；
// 内部服务 JWT → sub（如 manager-platform）；未认证返回 "-"
static string GetCaller(HttpContext context) =>
    context.User.Identity?.IsAuthenticated == true
        ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
          ?? context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
          ?? "-"
        : "-";

// 供 WebApplicationFactory<Program> 集成测试使用
public partial class Program { }
