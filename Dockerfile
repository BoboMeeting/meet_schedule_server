# syntax=docker/dockerfile:1

# ============================================================================
# 阶段 1：构建
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

# 先拷 csproj 利用层缓存恢复 NuGet（依赖变化时只重新 restore 一次）
COPY src/MeetScheduleServer/MeetScheduleServer.csproj ./src/MeetScheduleServer/
RUN dotnet restore src/MeetScheduleServer/MeetScheduleServer.csproj

# 拷贝剩余源码并发布（Livekit.Rtc.Dotnet 含 Linux 原生二进制，容器内 Bot 可正常入会）
COPY src/ ./src/
RUN dotnet publish src/MeetScheduleServer/MeetScheduleServer.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# 发布健康检查小工具（运行时镜像无 curl，健康检查改用 dotnet 运行时探测 /healthz）
RUN dotnet publish src/HealthCheck/HealthCheck.csproj \
    -c Release \
    -o /app/healthcheck \
    /p:UseAppHost=false

# ============================================================================
# 阶段 2：运行时镜像（仅包含发布产物）
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0.9 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

# .NET 10 官方镜像内置非 root 用户 app（uid 1001）；调度状态在内存中，无需持久化目录
USER app

COPY --from=build --chown=app:app /app/publish ./
COPY --from=build --chown=app:app /app/healthcheck ./healthcheck

EXPOSE 5000

ENTRYPOINT ["dotnet", "MeetScheduleServer.dll"]
