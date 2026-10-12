# The SDK image is pinned to the exact version in global.json, so CI and container builds use
# the same SDK, and both images are pinned by digest so a rebuild cannot silently pick up a
# different base. Dependabot proposes updates to the tag and digest together.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json NuGet.Config Directory.Build.props Directory.Packages.props ./
COPY src/BestStories.Api/BestStories.Api.csproj src/BestStories.Api/packages.lock.json src/BestStories.Api/
RUN dotnet restore src/BestStories.Api/BestStories.Api.csproj --locked-mode
COPY src/BestStories.Api/ src/BestStories.Api/
RUN dotnet publish src/BestStories.Api/BestStories.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

# Chiseled images contain only the runtime and its dependencies (no shell or package manager)
# and run as a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "BestStories.Api.dll"]
