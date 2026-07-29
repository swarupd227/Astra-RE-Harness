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

# ─── runtime: minimal image ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# Phase 11.0.g — documentation export.
# pandoc: mkdocs zip (no-op), docx, and pdf (--pdf-engine=weasyprint) formats.
# weasyprint: HTML→PDF renderer; avoids a TeX distribution (~200 MB total).
# Both packages are in Debian Bookworm apt repos.
#
# libssl3 / libcurl4 / ca-certificates: LibGit2Sharp's bundled native
# libgit2 binary links against these at runtime. The dev/build stages
# derive from the full dotnet/sdk image, which ships them by default —
# git-URL ingest has always worked locally. This slim aspnet runtime
# image doesn't include them, which crashed the process with SIGSEGV
# (exit code 139) the instant anything touched a LibGit2Sharp type,
# before the app even printed its startup banner.
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
