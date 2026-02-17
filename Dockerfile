FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

ARG VERSION=1.0.0

# Copy csproj files and restore
COPY Digiplex.Core/*.csproj Digiplex.Core/
COPY Digiplex.Serial/*.csproj Digiplex.Serial/
COPY Digiplex.Mqtt/*.csproj Digiplex.Mqtt/
COPY Digiplex.Worker/*.csproj Digiplex.Worker/
COPY Digiplex.sln .
RUN dotnet restore

# Copy source and build
COPY . .
RUN dotnet publish Digiplex.Worker -c Release -o /app/publish --no-restore \
    /p:Version=${VERSION} \
    /p:AssemblyVersion=${VERSION}.0 \
    /p:FileVersion=${VERSION}.0 \
    /p:InformationalVersion=${VERSION}

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

# Install serial port dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    libc6 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Default configuration via environment variables
ENV Serial__PortName=/dev/ttyUSB0
ENV Serial__BaudRate=19200
ENV Serial__Password=0000
ENV Serial__PollIntervalMs=1000
ENV Mqtt__BrokerHost=localhost
ENV Mqtt__BrokerPort=1883
ENV Mqtt__TopicPrefix=digiplex
ENV Mqtt__EnableHomeAssistantDiscovery=true

LABEL org.opencontainers.image.source=https://github.com/adumat/digiplex2mqtt
LABEL org.opencontainers.image.description="Digiplex alarm panel to MQTT bridge"

ENTRYPOINT ["dotnet", "Digiplex.Worker.dll"]
