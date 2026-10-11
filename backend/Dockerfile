# API image. Build from this folder:  docker build -t product-management/api:local .
# The API applies its own database migrations when it starts, before it serves (Database:ApplyMigrationsOnStartup):
# the image needs no separate migration command or job.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, in its own layer, so it is cached until a project file or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY ProductManagement.Core/ProductManagement.Core.csproj ProductManagement.Core/
COPY ProductManagement.Infrastructure/ProductManagement.Infrastructure.csproj ProductManagement.Infrastructure/
COPY ProductManagement.Api/ProductManagement.Api.csproj ProductManagement.Api/
RUN dotnet restore ProductManagement.Api/ProductManagement.Api.csproj

# Only the projects the API needs; test projects and deployment files stay out of the image.
COPY ProductManagement.Core/ ProductManagement.Core/
COPY ProductManagement.Infrastructure/ ProductManagement.Infrastructure/
COPY ProductManagement.Api/ ProductManagement.Api/
RUN dotnet publish ProductManagement.Api/ProductManagement.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
# The aspnet image ships a non-root "app" user; never run the API as root.
USER $APP_UID
ENTRYPOINT ["dotnet", "ProductManagement.Api.dll"]
