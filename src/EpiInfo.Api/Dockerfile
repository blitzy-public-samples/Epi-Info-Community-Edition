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
# The build needs BuildKit, the default builder since Docker Engine 23, for RUN --mount. Its context must be the root
# of a git checkout, because the API stage reads the context's .git folder: a git archive export, or a worktree or
# submodule whose .git is a file, fails the build. The repository has no .dockerignore, so each stage copies manifests
# by name and reads sources through read-only bind mounts, which reach no layer. The web stage builds web/src as it
# stands in the checkout, and the API stage the C# sources git tracks as they stand there, so an image of committed
# content alone needs a clean checkout of the commit. The settings files appsettings.json and
# appsettings.Development.json always come from commit HEAD, as the API stage describes.

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
# src/ and the context's .git folder are bind-mounted read-only for this step alone, with a tmpfs holding the file
# list, so none of them reaches a layer. git is given the folder through GIT_DIR, because repository discovery refuses
# a repository that another user owns (safe.directory) and an explicit git dir does not, so the context's files may
# have any owner. Only an allowlist reaches /src. The *.cs files of the four projects that git's index tracks as
# regular files are copied from the working tree as they stand. appsettings.json and appsettings.Development.json are
# written from their blobs in commit HEAD, whose id the step prints, and GIT_NO_REPLACE_OBJECTS makes git ignore
# replacement refs (git replace), so they are HEAD's literal blobs and a local edit to either, staged or not, such as
# a connection string added for a local run, reaches no layer. Untracked and ignored files, among them a developer's
# bin/ and obj/, generated C#, .env files, certificates and keys, Properties/launchSettings.json and every other
# appsettings.*.json or *.config, reach no layer either, and the Web SDK's default **/*.json and **/*.config publish
# content finds only the two settings files. The build fails when git cannot read the index; when a selected *.cs path
# is one git quotes, is not a regular file in the index (symlink, submodule or unmerged path), or in the working tree
# is a symlink, lies under a symlinked folder or is missing; when HEAD names no commit; when either settings file is
# not a regular file in that commit (missing, symlink or submodule) or git cannot list or read it; and when a project
# folder yields no C# source.
RUN --mount=type=bind,source=src,target=/context/src \
    --mount=type=bind,source=.git,target=/context/.git \
    --mount=type=tmpfs,target=/tmp/tracked \
    cd /context \
    && export GIT_DIR=/context/.git GIT_NO_REPLACE_OBJECTS=1 \
    && if ! git -c core.quotePath=false \
            ls-files --stage -- src/EpiInfo.Domain src/EpiInfo.CheckCode src/EpiInfo.Data src/EpiInfo.Api \
            > /tmp/tracked/files; \
        then echo "git could not read the index of the build context's git repository, .git" >&2; exit 1; \
    fi \
    && tab=$(printf '\t') \
    && while IFS="$tab" read -r entry f; do \
        case "$f" in \
            \"*) echo "$f: git quotes this path, so it cannot be copied" >&2; exit 1 ;; \
            *.cs) ;; \
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
    && if ! commit=$(git rev-parse --verify --quiet 'HEAD^{commit}'); then \
        echo "the build context's git repository has no commit at HEAD" >&2; exit 1; \
    fi \
    && echo "appsettings.json and appsettings.Development.json are taken from commit $commit (HEAD)" \
    && for s in appsettings.json appsettings.Development.json; do \
        f=src/EpiInfo.Api/$s; \
        if ! mode=$(git ls-tree --format='%(objectmode)' "$commit" -- "$f"); then \
            echo "git could not list $f in commit HEAD" >&2; exit 1; \
        fi; \
        case "$mode" in \
            100644|100755) ;; \
            *) echo "$f is not a regular file in commit HEAD (missing, symlink or submodule)" >&2; exit 1 ;; \
        esac; \
        git cat-file blob "$commit:$f" > "/src/$f" \
            || { echo "git could not read $f from commit HEAD" >&2; exit 1; }; \
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
