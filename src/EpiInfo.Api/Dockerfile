# Container image of the Epi Info web data entry: the API with the built web client, served from one process.
#
# Build from the repository root, which is the build context:
#   docker build -f src/EpiInfo.Api/Dockerfile -t epiinfo-web .
# Run with host networking, because the API listens on 127.0.0.1 only (Kestrel:Endpoints:Http:Url in
# appsettings.json) and a port published from a container network cannot reach a loopback listener. First export
# ConnectionStrings__EpiInfo in the shell that runs Docker, then pass it by name:
#   docker run --rm --network host -e ConnectionStrings__EpiInfo epiinfo-web
# -e with a name and no = makes Docker copy the value from that shell's environment, so the command line carries no
# credential. The connection string is supplied only at run time; the image holds no connection string or password.
#
# The build needs BuildKit, the default builder since Docker Engine 23, for RUN --mount, and a git checkout as its
# context: the API stage reads .git/index, git's record of the tracked files and nothing else under .git, so that it
# copies only tracked C# sources and the two committed settings files. The repository has no .dockerignore, so no
# stage copies a whole folder: manifests are copied by name, and source folders are read through read-only bind mounts
# of the build context, which are never committed to a layer. A stage keeps only the allowlisted inputs it copies out
# of a mount, or the output it builds from one. Host build output (bin/, obj/, web/node_modules, web/dist), untracked
# and ignored files, .env files, certificates and keys, user and launch settings, and every settings file other than
# appsettings.json and appsettings.Development.json therefore reach no layer and are never published. A symlinked or
# missing API input fails the build, and a new kind of build or publish input must be added to the allowlist of its
# stage.

# Stage 1: the web client, built with Node 22 as web/package.json engines requires.
FROM node:22 AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/index.html web/tsconfig.json web/vite.config.ts ./
# web/src is bind-mounted read-only for this step alone, so none of its files persists in a layer and the bundle is
# the only output. tsc --noEmit && vite build; writes /src/web/dist.
RUN --mount=type=bind,source=web/src,target=/src/web/src npm run build

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
# src/ and the context's git index are bind-mounted read-only for this step alone, with a tmpfs as git's scratch, so
# none of them reaches a layer. Only the allowlist is copied out: the files of the four projects that the index tracks
# as regular files and that are *.cs sources or the two committed settings files, appsettings.json and
# appsettings.Development.json. Untracked and ignored files, among them a developer's bin/ and obj/, generated C#,
# .env files, certificates and keys, Properties/launchSettings.json and every other appsettings.*.json or *.config,
# thus reach no layer, and the Web SDK's default **/*.json and **/*.config publish content finds only the two settings
# files. A selected path that git quotes, that is a symlink, lies under a symlinked folder or is missing fails the
# build, as do an untracked settings file and a project folder that yields no C# source.
RUN --mount=type=bind,source=src,target=/context/src \
    --mount=type=bind,source=.git/index,target=/context/.git-index \
    --mount=type=tmpfs,target=/tmp/tracked \
    cd /context \
    && git init --quiet /tmp/tracked \
    && if ! GIT_DIR=/tmp/tracked/.git GIT_INDEX_FILE=/context/.git-index git -c core.quotePath=false \
            ls-files --stage -- src/EpiInfo.Domain src/EpiInfo.CheckCode src/EpiInfo.Data src/EpiInfo.Api \
            > /tmp/tracked/files; \
        then echo "git could not read the build context's index, .git/index" >&2; exit 1; \
    fi \
    && tab=$(printf '\t') \
    && while IFS="$tab" read -r entry f; do \
        case "$f" in \
            \"*) echo "$f: git quotes this path, so it cannot be copied" >&2; exit 1 ;; \
            *.cs|src/EpiInfo.Api/appsettings.json|src/EpiInfo.Api/appsettings.Development.json) ;; \
            *) continue ;; \
        esac; \
        case "$entry" in \
            "100644 "*" 0"|"100755 "*" 0") ;; \
            *) echo "$f is not a regular file in git's index (symlink, submodule or unmerged path)" >&2; exit 1 ;; \
        esac; \
        [ -f "$f" ] && [ "$(realpath -e -- "$f")" = "/context/$f" ] \
            || { echo "$f is not a regular file of the build context (symlink, symlinked folder or missing)" >&2; \
                exit 1; }; \
        cp --parents -t /src -- "$f" || exit 1; \
    done < /tmp/tracked/files \
    && for s in appsettings.json appsettings.Development.json; do \
        [ -f "/src/src/EpiInfo.Api/$s" ] || { echo "src/EpiInfo.Api/$s is not tracked by git" >&2; exit 1; }; \
    done \
    && for p in Domain CheckCode Data Api; do \
        [ -n "$(find "/src/src/EpiInfo.$p" -name '*.cs' -print -quit)" ] \
            || { echo "No C# source was copied for src/EpiInfo.$p" >&2; exit 1; }; \
    done
# The restore's obj/ is kept, so the publish's own restore has nothing left to do.
RUN dotnet publish src/EpiInfo.Api/EpiInfo.Api.csproj -c Release -o /app/publish -p:UseAppHost=false

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
