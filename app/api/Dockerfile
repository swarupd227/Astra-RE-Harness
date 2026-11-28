ARG DOTNET_VERSION=10.0

# ─── Base build/runtime stages ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS sdk
WORKDIR /src

# The API runs on .NET 10 (the base image). The .NET 8 SDK is installed
# alongside so the validation compile gate can still build packages that
# target net8.0 (the dotnet8 archetypes, angular-dotnet8's backend half).
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && dotnet --list-sdks

# Phase 11.0.g — documentation export tools needed in dev AND runtime.
# Installing in sdk so the dev stage (which derives from sdk) also has them.
RUN apt-get update && apt-get install -y --no-install-recommends \
    pandoc \
    weasyprint \
    && rm -rf /var/lib/apt/lists/*

# Restore (cached layer) — copy csproj first
COPY src/Astra.Api/Astra.Api.csproj Astra.Api/
RUN dotnet restore Astra.Api/Astra.Api.csproj

# Copy the rest
COPY src/. .

# ─── dev: dotnet watch run, source bind-mounted at /src ───────────────
FROM sdk AS dev
WORKDIR /src/Astra.Api
EXPOSE 8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV ASPNETCORE_URLS=http://+:8080
CMD ["dotnet", "watch", "run", "--no-launch-profile", "--no-hot-reload", "--non-interactive"]

# ─── build: produce a published binary ────────────────────────────────
FROM sdk AS build
WORKDIR /src/Astra.Api
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

# ─── runtime: the deployed image ───────────────────────────────────────
#
# Was mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} (runtime-only,
# no SDK) until this was found to leave CompileValidator/TestPackValidator
# completely broken in production for every target stack that builds
# in-process (dotnet8, dotnet10, and now angular-dotnet8's backend half):
# they shell out to `dotnet build`/`dotnet test`, which the aspnet image
# can't run at all ("No .NET SDKs were found") — this had been silently
# broken since deployment because java-spring (the only stack anyone had
# actually exercised through this gate) validates via the separate
# maven-sidecar container instead, never touching this in-process path.
# Base is now the full sdk image so those commands exist for real.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS runtime
WORKDIR /app

# .NET 8 SDK — mirrors the install in the sdk/dev stage above. Needed
# here too: compile validation runs against THIS image, and dotnet8
# packages need the 8.0 SDK next to the 10.0 one the base image carries.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && dotnet --list-sdks

# Node.js — angular-dotnet8's frontend half validates via `npm install` +
# `npm run build` (CompileValidator) and `npm test` / jest (TestPackValidator)
# in-process, same pattern as the dotnet commands above. NodeSource's setup
# script is the standard way to get a current Node onto Debian Bookworm
# (the sdk image's base) since Bookworm's own apt repo Node is too old for
# current Angular CLI.
RUN curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/* \
    && node --version && npm --version

# Phase 11.0.g — documentation export.
# pandoc: mkdocs zip (no-op), docx, and pdf (--pdf-engine=weasyprint) formats.
# weasyprint: HTML→PDF renderer; avoids a TeX distribution (~200 MB total).
# Both packages are in Debian Bookworm apt repos. fonts-dejavu-core: the face the quality report PDF names
# (Validation/Quality/QualityReport.cs), so its layout does not depend on whatever font the base image carries.
#
# libssl3 / libcurl4 / ca-certificates: LibGit2Sharp's bundled native
# libgit2 binary links against these at runtime. Kept explicit even though
# the full sdk base likely already carries them, so this doesn't silently
# regress if a future base-image change slims them back out — this is
# exactly the class of bug (a LibGit2Sharp SIGSEGV before the app even
# printed its startup banner) that happened once already when the runtime
# base didn't have them.
#
# python3-venv / python3-pip: react-python's backend half validates in-process
# like the npm half — a virtual environment per run, `pip install -r
# requirements.txt`, then an import of every module (CompileValidator) or
# pytest (TestPackValidator). See Validation/PythonRunner.cs.
RUN apt-get update && apt-get install -y --no-install-recommends \
    pandoc \
    weasyprint \
    fonts-dejavu-core \
    python3 \
    python3-venv \
    python3-pip \
    libssl3 \
    libcurl4 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Python static analysis for the QUALITY gate (Validation/Quality/PythonTools.cs): ruff (bugs, complexity) and
# bandit (security) in their own virtual environment, so a generated package's dependencies never meet them.
RUN python3 -m venv /opt/astra-pytools \
    && /opt/astra-pytools/bin/pip install --no-cache-dir "ruff==0.8.4" "bandit==1.8.0" \
    && /opt/astra-pytools/bin/ruff --version && /opt/astra-pytools/bin/bandit --version

# TypeScript / JavaScript static analysis for the QUALITY gate (Validation/Quality/JsTools.cs): ESLint with
# typescript-eslint, React's rules of hooks and eslint-plugin-security, exact pins in quality-tools/package.json,
# installed in their own folder beside the config the gate copies into each package it reads.
COPY quality-tools/package.json quality-tools/eslint.config.mjs /opt/astra-jstools/
RUN cd /opt/astra-jstools && npm install --omit=dev --no-audit --no-fund --loglevel=error \
    && node node_modules/eslint/bin/eslint.js --version

COPY --from=build /app .
# The archetype templates come straight from the source tree: `dotnet publish`
# strips *.razor files (the Razor SDK treats them as component inputs, not
# shippable content), which left the Blazor archetypes unloadable in
# production ("declared file … .razor missing on disk"). bin/ and obj/
# under the templates are kept out by .dockerignore.
COPY src/Astra.Api/Llm/Archetypes/ ./Llm/Archetypes/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# The commit this image was built from: pass --build-arg BUILD_SHA=$(git
# rev-parse --short HEAD) at build time; /health and /health/ready report it
# as `build`, which is the one external proof a deploy landed.
ARG BUILD_SHA=dev
ENV ASTRA_BUILD_SHA=${BUILD_SHA}
ENTRYPOINT ["dotnet", "Astra.Api.dll"]
