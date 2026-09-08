ARG DOTNET_VERSION=8.0

# ─── Base build/runtime stages ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS sdk
WORKDIR /src

# Phase 10.0 — install .NET 10 SDK alongside .NET 8 so the validation
# compile gate can build VB6 scaffolds (which target net10.0 per ADR-035).
# The Astra.Api project itself stays on net8.0; only the in-tmp builds
# triggered by /scaffolds/{id}/validate/compile use net10.0.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --quality preview --install-dir /usr/share/dotnet \
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

# .NET 10 SDK — mirrors the install in the sdk/dev stage above. Needed
# here too: VB6 scaffolds target net10.0 (ADR-035), and compile validation
# for them runs against THIS image, not the build-time sdk stage.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --quality preview --install-dir /usr/share/dotnet \
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
# Both packages are in Debian Bookworm apt repos.
#
# libssl3 / libcurl4 / ca-certificates: LibGit2Sharp's bundled native
# libgit2 binary links against these at runtime. Kept explicit even though
# the full sdk base likely already carries them, so this doesn't silently
# regress if a future base-image change slims them back out — this is
# exactly the class of bug (a LibGit2Sharp SIGSEGV before the app even
# printed its startup banner) that happened once already when the runtime
# base didn't have them.
RUN apt-get update && apt-get install -y --no-install-recommends \
    pandoc \
    weasyprint \
    libssl3 \
    libcurl4 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Astra.Api.dll"]
