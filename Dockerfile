# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY . ./
RUN dotnet restore src/apps/DubboTester.Web/DubboTester.csproj

RUN dotnet publish src/apps/DubboTester.Web/DubboTester.csproj \
    --configuration ${BUILD_CONFIGURATION} \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    && test -s /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

COPY --from=build /app/publish ./

# MCP configuration is written to /app/App_Data/mcp-config.json at runtime.
USER root
RUN mkdir -p /app/App_Data \
    && chown -R ${APP_UID}:${APP_UID} /app
USER ${APP_UID}

ENTRYPOINT ["dotnet", "DubboTester.dll"]
