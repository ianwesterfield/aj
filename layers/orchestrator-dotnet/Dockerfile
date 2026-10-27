# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY AJ.Orchestrator.API/*.csproj AJ.Orchestrator.API/
COPY AJ.Orchestrator.Abstractions/*.csproj AJ.Orchestrator.Abstractions/
COPY AJ.Orchestrator.Domain/*.csproj AJ.Orchestrator.Domain/
COPY AJ.Shared/*.csproj AJ.Shared/

# Restore dependencies
RUN dotnet restore AJ.Orchestrator.API/AJ.Orchestrator.API.csproj

# Copy source code
COPY AJ.Orchestrator.API/ AJ.Orchestrator.API/
COPY AJ.Orchestrator.Abstractions/ AJ.Orchestrator.Abstractions/
COPY AJ.Orchestrator.Domain/ AJ.Orchestrator.Domain/
COPY AJ.Shared/ AJ.Shared/

# Build and publish
WORKDIR /src/AJ.Orchestrator.API
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Install ICU for globalization support
RUN apk add --no-cache icu-libs

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_URLS=http://+:8004
ENV ASPNETCORE_ENVIRONMENT=Production

# Copy published output
COPY --from=build /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:8004/health || exit 1

EXPOSE 8004

ENTRYPOINT ["dotnet", "AJ.Orchestrator.API.dll"]