FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/VideoArchiveManager/VideoArchiveManager.csproj
RUN dotnet publish src/VideoArchiveManager/VideoArchiveManager.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends yt-dlp ffmpeg && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "VideoArchiveManager.dll"]