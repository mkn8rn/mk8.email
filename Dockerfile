# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY . .
RUN dotnet restore mk8.email.CLI/mk8.email.Application.CLI.csproj --locked-mode
RUN dotnet publish mk8.email.CLI/mk8.email.Application.CLI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --property:ContinuousIntegrationBuild=true \
    --property:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.11-noble AS final
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0 \
    MK8EMAIL_CONFIG_FILE=/run/secrets/mk8email_config

COPY --from=build --chown=app:app /app/publish .

USER app

EXPOSE 2525 2587 2465 2143 2993

HEALTHCHECK --interval=30s --timeout=12s --start-period=30s --retries=3 \
    CMD ["dotnet", "mk8.email.Application.CLI.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "mk8.email.Application.CLI.dll"]
