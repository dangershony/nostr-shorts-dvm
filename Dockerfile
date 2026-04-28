# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/NostrShortsDvm/NostrShortsDvm.csproj src/NostrShortsDvm/
RUN dotnet restore src/NostrShortsDvm/NostrShortsDvm.csproj
COPY src/ src/
RUN dotnet publish src/NostrShortsDvm/NostrShortsDvm.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# Install yt-dlp and ffmpeg
RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 ffmpeg curl && \
    curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && \
    chmod a+rx /usr/local/bin/yt-dlp && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Create directories for data and temp files
RUN mkdir -p /app/data /app/temp

ENTRYPOINT ["dotnet", "NostrShortsDvm.dll"]
