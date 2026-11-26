# Container image of the Epi Info web data entry: the API with the built web client, served from one process.
#
# Build from the repository root, which is the build context:
#   docker build -f src/EpiInfo.Api/Dockerfile -t epiinfo-web .
# Run with host networking, because the API listens on 127.0.0.1 only (Kestrel:Endpoints:Http:Url in
# appsettings.json) and a port published from a container network cannot reach a loopback listener:
#   docker run --rm --network host -e ConnectionStrings__EpiInfo='<connection string>' epiinfo-web
# The connection string is supplied only at run time; the image holds no connection string or password.
#
# The repository has no .dockerignore, so every stage copies the files it needs by name, and host build output
# (web/node_modules, web/dist, bin/, obj/) never reaches a build step or the image.

# Stage 1: the web client, built with Node 22 as web/package.json engines requires.
FROM node:22 AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/index.html web/tsconfig.json web/vite.config.ts ./
COPY web/src/ ./src/
# tsc --noEmit && vite build; writes /src/web/dist.
RUN npm run build

# Stage 2: the API, published with an SDK of the 10.0.1xx feature band. global.json pins 10.0.100 with
# rollForward latestPatch, so the sdk:10.0 tag, which carries a later feature band, fails the SDK check.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103 AS api
WORKDIR /src
COPY global.json Directory.Packages.props ./
COPY src/Directory.Build.props src/
COPY src/EpiInfo.Domain/EpiInfo.Domain.csproj src/EpiInfo.Domain/
COPY src/EpiInfo.CheckCode/EpiInfo.CheckCode.csproj src/EpiInfo.CheckCode/
COPY src/EpiInfo.Data/EpiInfo.Data.csproj src/EpiInfo.Data/
COPY src/EpiInfo.Api/EpiInfo.Api.csproj src/EpiInfo.Api/
# A layer of its own, so a source change reuses the downloaded packages.
RUN dotnet restore src/EpiInfo.Api/EpiInfo.Api.csproj
COPY src/EpiInfo.Domain/ src/EpiInfo.Domain/
COPY src/EpiInfo.CheckCode/ src/EpiInfo.CheckCode/
COPY src/EpiInfo.Data/ src/EpiInfo.Data/
COPY src/EpiInfo.Api/ src/EpiInfo.Api/
# The project folders may carry a developer's bin/ and obj/, whose stale outputs would reach the publish. Removing
# them also drops the restore's obj/; the publish restores again from the packages the layer above downloaded.
RUN rm -rf src/EpiInfo.*/bin src/EpiInfo.*/obj \
    && dotnet publish src/EpiInfo.Api/EpiInfo.Api.csproj -c Release -o /app/publish -p:UseAppHost=false

# Stage 3: the runtime. The content root is /app, so the published appsettings.json applies, and its committed
# endpoint http://127.0.0.1:5000 is the only listener.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app/publish ./
COPY --from=web /src/web/dist ./client/
# Web__ClientPath points the API at the client copied above. ASPNETCORE_HTTP_PORTS is cleared, not removed: the base
# image sets it to 8080, which binds every interface and which the API's loopback guard refuses at startup, while an
# empty listen setting counts as absent. ASPNETCORE_URLS is left unset for the same reason, and no port is exposed.
ENV Web__ClientPath=/app/client \
    ASPNETCORE_HTTP_PORTS=
# The base image's non-root user, app; save stamps written from the container carry that name.
USER $APP_UID
ENTRYPOINT ["dotnet", "EpiInfo.Api.dll"]
