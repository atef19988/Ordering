# Build context is the repository root (Directory.*.props and global.json live there):
#   docker build -f src/Ordering.Api/Dockerfile -t ordering-api .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first so the package layer is cached while source changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ordering.Domain/Ordering.Domain.csproj                 src/Ordering.Domain/
COPY src/Ordering.Application/Ordering.Application.csproj       src/Ordering.Application/
COPY src/Ordering.Infrastructure/Ordering.Infrastructure.csproj src/Ordering.Infrastructure/
COPY src/Ordering.Api/Ordering.Api.csproj                       src/Ordering.Api/
RUN dotnet restore src/Ordering.Api/Ordering.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ordering.Api/Ordering.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
# curl is only for the container healthcheck (GET /health); the base image has no HTTP client.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

# Plain HTTP inside the compose network; TLS is the reverse proxy's job.
ENV ASPNETCORE_HTTP_PORTS=5000
EXPOSE 5000
USER app
ENTRYPOINT ["dotnet", "Ordering.Api.dll"]
