# === Shared build stage ===
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-base
WORKDIR /src

COPY Digiplex.Core/*.csproj Digiplex.Core/
COPY Digiplex.Serial/*.csproj Digiplex.Serial/
COPY Digiplex.Mqtt/*.csproj Digiplex.Mqtt/
COPY Digiplex.Worker/*.csproj Digiplex.Worker/
# Restore only the Worker (+ its transitive project refs) — NOT the whole solution,
# which now also contains Digiplex.Tests (test project, not part of the image).
RUN dotnet restore Digiplex.Worker/Digiplex.Worker.csproj

COPY . .

# === Release publish ===
FROM build-base AS build-release
ARG VERSION=1.0.0
RUN dotnet publish Digiplex.Worker -c Release -o /app/publish --no-restore \
    /p:Version=${VERSION} \
    /p:AssemblyVersion=${VERSION}.0 \
    /p:FileVersion=${VERSION}.0 \
    /p:InformationalVersion=${VERSION}

# === Debug publish ===
FROM build-base AS build-debug
RUN dotnet publish Digiplex.Worker -c Debug -o /app/publish --no-restore

# === Shared runtime base ===
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime-base
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libc6 \
    && rm -rf /var/lib/apt/lists/*

ENV Serial__PortName=/dev/ttyUSB0
ENV Serial__BaudRate=19200
ENV Serial__Password=0000
ENV Serial__PollIntervalMs=1000
ENV Mqtt__BrokerHost=localhost
ENV Mqtt__BrokerPort=1883
ENV Mqtt__TopicPrefix=digiplex
ENV Mqtt__EnableHomeAssistantDiscovery=true

LABEL org.opencontainers.image.source=https://github.com/adumat/paradox-dgp-mqtt
LABEL org.opencontainers.image.description="Digiplex alarm panel to MQTT bridge"

# === Release runtime (default target) ===
FROM runtime-base AS release
COPY --from=build-release /app/publish .
ENTRYPOINT ["dotnet", "Digiplex.Worker.dll"]

# === Debug runtime ===
FROM runtime-base AS debug
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg \
    && apt-get purge -y curl && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build-debug /app/publish .
ENTRYPOINT ["dotnet", "Digiplex.Worker.dll"]
