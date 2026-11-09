FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /source

# Restore from project files first so NuGet packages remain cached when only source code changes.
COPY Directory.Build.props global.json ./
COPY src/HappyMix.Bot/HappyMix.Bot.csproj src/HappyMix.Bot/
COPY src/HappyMix.Application/HappyMix.Application.csproj src/HappyMix.Application/
COPY src/HappyMix.Domain/HappyMix.Domain.csproj src/HappyMix.Domain/
COPY src/HappyMix.Infrastructure/HappyMix.Infrastructure.csproj src/HappyMix.Infrastructure/
RUN dotnet restore src/HappyMix.Bot/HappyMix.Bot.csproj

COPY src/ src/
RUN dotnet publish src/HappyMix.Bot/HappyMix.Bot.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.2 AS final
WORKDIR /app

# Named volumes inherit this writable directory on first creation.
USER root
RUN mkdir -p /app/data && chown app:app /app/data
ENV Storage__DatabasePath=/app/data/happymix.db

# The official .NET runtime image provides the non-root "app" user.
USER app
COPY --from=build --chown=app:app /app/publish ./

ENTRYPOINT ["dotnet", "HappyMix.Bot.dll"]
