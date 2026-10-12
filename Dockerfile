# global.json is deliberately not copied: it pins a newer SDK feature band than the SDK image
# ships, and the image's SDK is sufficient to build the net10.0 target.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/BestStories.Api/BestStories.Api.csproj src/BestStories.Api/
RUN dotnet restore src/BestStories.Api/BestStories.Api.csproj
COPY src/BestStories.Api/ src/BestStories.Api/
RUN dotnet publish src/BestStories.Api/BestStories.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

# Chiseled images contain only the runtime and its dependencies (no shell or package manager)
# and run as a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BestStories.Api.dll"]
