FROM debian:bookworm-slim AS base

WORKDIR /app

# .NET 10 SDK for compile-and-run, Python 3 + pip for the REST server.
# dotnet-install.sh requires curl + ca-certificates; python3-venv is preferred
# over --break-system-packages for pip on bookworm.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

# Install .NET 10 SDK via the official install script.
# Using the feed channel so RC/GA is picked automatically.
ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH=$DOTNET_ROOT:$PATH
# Invariant-culture mode avoids the libicu dependency on bookworm-slim.
# The sidecar does no locale-sensitive work so this is safe.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
RUN curl -sSL https://dot.net/v1/dotnet-install.sh \
        -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel 10.0 --install-dir $DOTNET_ROOT --no-path \
 && rm /tmp/dotnet-install.sh

# Warm-up: restore the NuGet cache once so first compile is fast.
# We create a throw-away project and restore its packages; this populates
# ~/.nuget/packages before any user source arrives.
RUN mkdir -p /tmp/dotnet-warmup \
 && dotnet new classlib -n Warmup -o /tmp/dotnet-warmup --no-restore \
 && dotnet restore /tmp/dotnet-warmup/Warmup.csproj \
 && rm -rf /tmp/dotnet-warmup

# Python FastAPI REST server (matches the gpp-sidecar contract verbatim).
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

COPY csharp_sidecar/ csharp_sidecar/

ENV PYTHONUNBUFFERED=1 \
    CSHARP_PORT=51059 \
    CSHARP_WORKDIR=/var/tmp/csharp-runs \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

EXPOSE 51059

CMD ["python3", "-m", "csharp_sidecar.server"]
