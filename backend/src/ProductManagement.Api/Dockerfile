# Build context: ./backend (see docker-compose.yml). Directory.*.props and global.json must be in the context.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, in its own layer, so it is cached until a project file or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ProductManagement.Domain/ProductManagement.Domain.csproj src/ProductManagement.Domain/
COPY src/ProductManagement.Application/ProductManagement.Application.csproj src/ProductManagement.Application/
COPY src/ProductManagement.Infrastructure/ProductManagement.Infrastructure.csproj src/ProductManagement.Infrastructure/
COPY src/ProductManagement.Api/ProductManagement.Api.csproj src/ProductManagement.Api/
RUN dotnet restore src/ProductManagement.Api/ProductManagement.Api.csproj

COPY src/ src/
RUN dotnet publish src/ProductManagement.Api/ProductManagement.Api.csproj \
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
