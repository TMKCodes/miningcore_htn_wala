# Build stage
FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy AS builder
WORKDIR /app

# Install build dependencies
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    cmake ninja-build build-essential \
    libssl-dev pkg-config libboost-all-dev \
    libsodium-dev libzmq3-dev \
    libgmp-dev golang-go && \
    rm -rf /var/lib/apt/lists/*

COPY . .
WORKDIR /app/src/Miningcore
RUN dotnet publish -c Release --framework net6.0 --self-contained false -o ../../build

# Runtime stage (minimal)
FROM mcr.microsoft.com/dotnet/aspnet:6.0-jammy

WORKDIR /app

# Runtime libraries only
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    libzmq5 \
    libsodium23 \
    libgmp10 \
    curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

EXPOSE 4000-4090

COPY --from=builder /app/build ./

ENTRYPOINT ["./Miningcore", "-c", "config.json"]