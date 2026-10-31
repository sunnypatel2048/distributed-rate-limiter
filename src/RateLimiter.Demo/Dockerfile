# Build context is the repo root:
#   docker build -f src/RateLimiter.Demo/Dockerfile .

# ---- build stage: full SDK, thrown away afterwards ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only the project files. This layer is cached until a .csproj
# changes, so day-to-day code edits don't re-download every NuGet package.
COPY src/RateLimiter.Core/RateLimiter.Core.csproj src/RateLimiter.Core/
COPY src/RateLimiter.Middleware/RateLimiter.Middleware.csproj src/RateLimiter.Middleware/
COPY src/RateLimiter.Demo/RateLimiter.Demo.csproj src/RateLimiter.Demo/
RUN dotnet restore src/RateLimiter.Demo/RateLimiter.Demo.csproj

COPY src/ src/
RUN dotnet publish src/RateLimiter.Demo/RateLimiter.Demo.csproj \
    --configuration Release --no-restore --output /app/publish

# ---- runtime stage: ASP.NET runtime only, much smaller ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# curl is only for the container health check (docker-compose.yml): the ASP.NET
# runtime image ships without an HTTP client.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The official .NET images ship a non-root "app" user; don't run the API as root.
USER $APP_UID

# .NET 8+ images listen on 8080 by default (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
ENTRYPOINT ["dotnet", "RateLimiter.Demo.dll"]
