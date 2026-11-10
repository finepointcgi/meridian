# Meridian — multi-stage build. Run with:  docker compose --profile app up -d --build   → http://localhost:8180
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Meridian.slnx ./
COPY src/Meridian.Core/Meridian.Core.csproj src/Meridian.Core/
COPY src/Meridian.Serialization/Meridian.Serialization.csproj src/Meridian.Serialization/
COPY src/Meridian.Data/Meridian.Data.csproj src/Meridian.Data/
COPY src/Meridian.Web/Meridian.Web.csproj src/Meridian.Web/
COPY src/Meridian.FmuHost/Meridian.FmuHost.csproj src/Meridian.FmuHost/
RUN dotnet restore src/Meridian.Web/Meridian.Web.csproj
COPY src/ src/
RUN dotnet publish src/Meridian.Web/Meridian.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Fonts + fontconfig for PDF/Word diagram rendering (QuestPDF / Skia).
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    Meridian__KeysPath=/data/keys
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Meridian.Web.dll"]
