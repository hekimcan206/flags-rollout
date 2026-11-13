# syntax=docker/dockerfile:1

# One build, three images: `--target controller`, `--target demo-api`, `--target loadgen`.

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/FlagsRollout.Core/FlagsRollout.Core.csproj src/FlagsRollout.Core/
COPY src/FlagsRollout.Controller/FlagsRollout.Controller.csproj src/FlagsRollout.Controller/
COPY src/FlagsRollout.DemoApi/FlagsRollout.DemoApi.csproj src/FlagsRollout.DemoApi/
COPY samples/FlagsRollout.LoadGen/FlagsRollout.LoadGen.csproj samples/FlagsRollout.LoadGen/
RUN --mount=type=cache,id=flags-rollout-nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet restore src/FlagsRollout.Controller/FlagsRollout.Controller.csproj && \
    dotnet restore src/FlagsRollout.DemoApi/FlagsRollout.DemoApi.csproj && \
    dotnet restore samples/FlagsRollout.LoadGen/FlagsRollout.LoadGen.csproj

COPY .editorconfig ./
COPY src/ src/
COPY samples/ samples/
RUN --mount=type=cache,id=flags-rollout-nuget,sharing=locked,target=/root/.nuget/packages \
    dotnet publish src/FlagsRollout.Controller/FlagsRollout.Controller.csproj -c Release --no-restore -o /app/controller -p:UseAppHost=false && \
    dotnet publish src/FlagsRollout.DemoApi/FlagsRollout.DemoApi.csproj -c Release --no-restore -o /app/demo-api -p:UseAppHost=false && \
    dotnet publish samples/FlagsRollout.LoadGen/FlagsRollout.LoadGen.csproj -c Release --no-restore -o /app/loadgen -p:UseAppHost=false && \
    mkdir -p /app/empty

# ---- runtime images ----------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS controller
LABEL org.opencontainers.image.title="flags-rollout-controller" \
      org.opencontainers.image.source="https://github.com/hekimcan206/flags-rollout" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/controller .
# The shared flag volume is created from this directory, so the non-root controller owns it and can replace the file.
COPY --from=build --chown=1654:1654 /app/empty /flags
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FlagsRollout.Controller.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS demo-api
LABEL org.opencontainers.image.title="flags-rollout-demo-api" \
      org.opencontainers.image.source="https://github.com/hekimcan206/flags-rollout" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/demo-api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FlagsRollout.DemoApi.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS loadgen
LABEL org.opencontainers.image.title="flags-rollout-loadgen" \
      org.opencontainers.image.source="https://github.com/hekimcan206/flags-rollout" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/loadgen .
USER $APP_UID
ENTRYPOINT ["dotnet", "FlagsRollout.LoadGen.dll"]
