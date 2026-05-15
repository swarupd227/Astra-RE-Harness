ARG DOTNET_VERSION=8.0

# ─── Base build/runtime stages ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS sdk
WORKDIR /src

# Restore (cached layer) — copy csproj first
COPY src/Astra.Api/Astra.Api.csproj src/Astra.Api/
RUN dotnet restore src/Astra.Api/Astra.Api.csproj

# Copy the rest
COPY src/. src/

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
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Astra.Api.dll"]
