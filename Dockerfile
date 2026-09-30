# AuthBridge API for Render: controller API, hosted /mcp and the payer simulator in one
# container. Multi-stage; the runtime stage runs as the non-root user the .NET image provides.
# Migration assemblies and the DbTool are deliberately not in this image: migrations are a
# separate, reviewed operator step (docs/DATABASE_MIGRATION.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AuthBridge.Domain/ src/AuthBridge.Domain/
COPY src/AuthBridge.Application/ src/AuthBridge.Application/
COPY src/AuthBridge.Infrastructure/ src/AuthBridge.Infrastructure/
COPY src/AuthBridge.Migrations.Postgres/ src/AuthBridge.Migrations.Postgres/
COPY src/AuthBridge.Mcp/ src/AuthBridge.Mcp/
COPY src/AuthBridge.Api/ src/AuthBridge.Api/
RUN dotnet restore src/AuthBridge.Api/AuthBridge.Api.csproj --locked-mode
RUN dotnet publish src/AuthBridge.Api/AuthBridge.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
COPY --from=build /app .
# Development-only settings (LocalDB, LocalDev signing key, stdio subject) never ship.
RUN rm -f appsettings.Development.json
USER $APP_UID
EXPOSE 8080
# Program.cs binds 0.0.0.0:$PORT when Render sets PORT, otherwise 8080.
ENTRYPOINT ["dotnet", "AuthBridge.Api.dll"]
