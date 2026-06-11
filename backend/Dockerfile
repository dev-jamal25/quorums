# Single publish output for api + worker (zero API<->worker version skew).
# Both Api.dll and Worker.dll land in /app/publish; the runtime image selects
# which to run via entrypoint/command (api = default; worker overrides in compose).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the central manifests first for layer caching.
COPY Directory.Build.props Directory.Packages.props Backend.sln ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Backend.sln

# Publish Api and Worker into ONE shared output folder.
RUN dotnet publish src/Api/Api.csproj       -c Release -o /app/publish --no-restore
RUN dotnet publish src/Worker/Worker.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Default to the API; the worker service overrides the command in docker-compose.
EXPOSE 8080
ENTRYPOINT ["dotnet", "Backend.Api.dll"]
