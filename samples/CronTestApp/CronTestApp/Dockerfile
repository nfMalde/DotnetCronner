# Runtime stage (also what VS uses in fast/debug mode).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 8080

# The custom JSON store (CRONNER_STORE=custom) writes here; docker compose mounts a volume over it,
# which inherits this ownership so the non-root app user can write.
USER root
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
USER $APP_UID


# Build stage. The docker-compose build context is the repository ROOT (see docker-compose.yml), so the
# whole repo — both src/ and samples/CronTestApp/ — lands under /src and the app's ..\..\..\src\* project
# references resolve exactly as they do on the host. The repo-root .dockerignore keeps bin/obj/.vs/.git out.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY . .
# Belt-and-braces: drop any build intermediates that slipped past .dockerignore.
RUN find /src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

WORKDIR /src/samples/CronTestApp
RUN dotnet restore "CronTestApp/CronTestApp.csproj"
RUN dotnet build "CronTestApp/CronTestApp.csproj" -c $BUILD_CONFIGURATION -o /app/build


# Publish stage.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR /src/samples/CronTestApp
RUN dotnet publish "CronTestApp/CronTestApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false


# Final image.
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CronTestApp.dll"]
