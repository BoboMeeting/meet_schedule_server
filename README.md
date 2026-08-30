# MeetScheduleServer — 基于 LiveKit 的视频会议调度服务（C#）

调度服务骨架：定时创建会议房间、派 Bot 以真实参会者身份入会、通过 Data Channel 收发数据（SendData）、接收并验签 LiveKit Webhook 事件。

## 开发规范

1. **每次修改都要提交 GIT**，以便回溯和跟踪。
2. 提交前确保 `dotnet build` 与 `dotnet test` 全部通过。

## 技术栈

- .NET 10 / ASP.NET Core Minimal API
- [Livekit.Server.Sdk.Dotnet](https://www.nuget.org/packages/Livekit.Server.Sdk.Dotnet)：服务端 API（JWT 签发、房间管理、服务端 SendData、Webhook 验签）
- [Livekit.Rtc.Dotnet](https://www.nuget.org/packages/Livekit.Rtc.Dotnet)：RTC 客户端（Bot 以 WebRTC 参会者身份入会、Data Channel 收发）
- xUnit + WebApplicationFactory：单元测试与集成测试（Fake 替换 LiveKit 依赖，不依赖真实服务器）

## 目录结构

```
src/MeetScheduleServer/
├── Program.cs                  # 入口：DI 注册 + 路由映射
├── Options/                    # LiveKitOptions / SchedulerOptions
├── Models/Schedule.cs          # 调度任务模型（Schedule / BotConfig / BotHandle 等）
├── Stores/InMemoryScheduleStore.cs  # 内存存储（生产可替换为数据库实现 IScheduleStore）
├── LiveKit/
│   ├── LiveKitServerApi.cs     # Twirp 服务端 API：CreateRoom / SendData / ListParticipants
│   └── LiveKitWebhookService.cs# Webhook 验签（WebhookReceiver）
├── Bots/
│   ├── RtcBotDriver.cs         # 真实 Bot 驱动：入会 / PublishData / 离会（IBotDriver）
│   └── BotManager.cs           # 按 scheduleId 管理 Bot 生命周期
├── Scheduling/SchedulerService.cs  # 后台轮询：到期启动 / 超时结束 / 取消 / Webhook 联动
└── Endpoints/
    ├── ScheduleEndpoints.cs    # /api/schedules CRUD + start
    ├── DataEndpoints.cs        # bot SendData + 服务端 SendData
    └── WebhookEndpoint.cs      # /webhook/livekit（raw body 验签）

tests/MeetScheduleServer.Tests/
├── ScheduleStoreTests.cs       # 存储层
├── SchedulerServiceTests.cs    # 调度逻辑（Fake 驱动）
└── EndpointsTests.cs           # HTTP 集成测试（Fake 替换 LiveKit）
```

## HTTP API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/healthz` | 健康检查 |
| POST | `/api/schedules` | 创建调度（`roomName` 必填；`startAt` 缺省立即开始） |
| GET | `/api/schedules` / `/{id}` | 列表 / 详情 |
| POST | `/api/schedules/{id}/start` | 立即启动（创建房间 + Bot 入会） |
| DELETE | `/api/schedules/{id}` | 取消（Bot 离会） |
| POST | `/api/schedules/{id}/data` | Bot SendData（Data Channel，可指定 topic / 目标参会者） |
| POST | `/api/rooms/{room}/data` | 服务端 SendData（无需 Bot 入会，直接向房间注入数据） |
| POST | `/webhook/livekit` | LiveKit Webhook（`room_started` / `room_finished` / `participant_joined` / `participant_left`） |

## 配置

`appsettings.json`（生产用环境变量 `LiveKit__ApiSecret` 或 user-secrets 覆盖）：

```json
{
  "LiveKit": {
    "Url": "ws://localhost:7880",
    "ApiKey": "devkey",
    "ApiSecret": "必须 ≥ 256 位，否则 Webhook 验签器无法构造"
  },
  "Scheduler": {
    "PollIntervalSeconds": 1,
    "BotIdentityPrefix": "bot"
  }
}
```

LiveKit 侧 `livekit.yaml` 需配置 webhook 指向本服务：

```yaml
webhook:
  urls: http://<host>:<port>/webhook/livekit
  api_key: devkey
```

## 运行与测试

```bash
dotnet run --project src/MeetScheduleServer    # 启动
dotnet test tests/MeetScheduleServer.Tests     # 测试（19 个用例，无需真实 LiveKit）
```

## 设计要点

- **Webhook 必须读 raw body 验签**：路由手动读 `HttpRequest.Body` 后交给 `WebhookReceiver.Receive`，不能经过 JSON 模型绑定。
- **Bot 是真实参会者**：通过 LiveKit.Rtc SDK 走 WebRTC 连接入会，能监听房间事件（参会者进出、数据到达），并可主动 PublishData。
- **双通道 SendData**：Bot SendData（参会者身份）与服务端 SendData（Twirp API）分别覆盖定向消息与全局注入两类场景。
- **可替换抽象**：`IScheduleStore` / `ILiveKitServerApi` / `IBotDriver` 均为接口，测试用 Fake 替换；生产可平滑接入数据库与真实 LiveKit。
- **生产化提示**：单实例内存轮询；多实例部署时需持久化存储 + 分布式锁或数据库轮询。
