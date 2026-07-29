ARG DOTNET_VERSION=8.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS sdk
WORKDIR /src

COPY src/Astra.Worker/Astra.Worker.csproj Astra.Worker/
RUN dotnet restore Astra.Worker/Astra.Worker.csproj

COPY src/. .

# ─── dev: dotnet watch run ────────────────────────────────────────────
FROM sdk AS dev
WORKDIR /src/Astra.Worker
EXPOSE 8081
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV ASPNETCORE_URLS=http://+:8081
CMD ["dotnet", "watch", "run", "--no-launch-profile", "--no-hot-reload", "--non-interactive"]

# ─── build/runtime ────────────────────────────────────────────────────
FROM sdk AS build
WORKDIR /src/Astra.Worker
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8081
ENV ASPNETCORE_URLS=http://+:8081
ENTRYPOINT ["dotnet", "Astra.Worker.dll"]
