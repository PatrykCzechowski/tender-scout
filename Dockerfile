# One build stage for the four services, then one small runtime image for each.
# Build an image by naming its stage: docker build --target api --tag tender-scout-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore before the source is copied. This layer depends only on the files that decide the packages,
# so a change in a .cs file reuses it instead of downloading every package again.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TenderScout.Api/TenderScout.Api.csproj src/TenderScout.Api/
COPY src/TenderScout.Mcp/TenderScout.Mcp.csproj src/TenderScout.Mcp/
COPY src/TenderScout.Worker/TenderScout.Worker.csproj src/TenderScout.Worker/
COPY src/TenderScout.MigrationService/TenderScout.MigrationService.csproj src/TenderScout.MigrationService/
COPY src/TenderScout.Infrastructure/TenderScout.Infrastructure.csproj src/TenderScout.Infrastructure/
COPY src/TenderScout.ServiceDefaults/TenderScout.ServiceDefaults.csproj src/TenderScout.ServiceDefaults/
COPY src/TenderScout.Domain/TenderScout.Domain.csproj src/TenderScout.Domain/
COPY src/TenderScout.Contracts/TenderScout.Contracts.csproj src/TenderScout.Contracts/
RUN dotnet restore src/TenderScout.Api \
    && dotnet restore src/TenderScout.Mcp \
    && dotnet restore src/TenderScout.Worker \
    && dotnet restore src/TenderScout.MigrationService

# Framework-dependent and without a runtime identifier: the same file builds on amd64 and arm64.
COPY src/ src/
RUN dotnet publish src/TenderScout.Api --configuration Release --no-restore --output /app/api \
    && dotnet publish src/TenderScout.Mcp --configuration Release --no-restore --output /app/mcp \
    && dotnet publish src/TenderScout.Worker --configuration Release --no-restore --output /app/worker \
    && dotnet publish src/TenderScout.MigrationService --configuration Release --no-restore --output /app/migrations

# The runtime images hold the published output and nothing else: no SDK, no source.
# All four use the ASP.NET Core image, also the worker and the migrations, because the shared
# service defaults reference ASP.NET Core. It brings the time zone data and ICU the code needs.
# The base image has the user "app" but starts as root, so each stage switches to that user.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TenderScout.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS mcp
WORKDIR /app
COPY --from=build /app/mcp .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TenderScout.Mcp.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS worker
WORKDIR /app
COPY --from=build /app/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "TenderScout.Worker.dll"]

# Applies the database migrations and exits. Run it once, before the other three.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrations
WORKDIR /app
COPY --from=build /app/migrations .
USER $APP_UID
ENTRYPOINT ["dotnet", "TenderScout.MigrationService.dll"]
