#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────
# Astra RE Harness — manual Azure App Service deployment (full stack)
# ─────────────────────────────────────────────────────────────────────
#
# Deploys every docker-compose service as its own Linux App Service:
#   api, worker, frontend, minio, and all 9 language-validation sidecars
#   (parser, gfortran, maven, gnucobol, fpc, gpp, vb6, csharp, property-test).
# Postgres is NOT containerized — it's a managed Azure Database for
# PostgreSQL Flexible Server, which is the sound choice for a stateful
# backing service (App Service containers have no reliable durable
# storage of their own).
#
# This script is meant to be run top-to-bottom, by hand, from a bash
# shell with the Azure CLI (`az`) installed and Docker NOT required —
# every image is built in the cloud via `az acr build`.
#
# BEFORE RUNNING:
#   1. Fill in the "EDIT ME" variables below (passwords, secrets, the
#      Anthropic API key). Never commit this file with real secrets in it.
#   2. Run from the repo's `app/` directory (paths below are relative to it).
#   3. `az login` interactively first, or the script's own `az login`
#      line will prompt you.
#
# KNOWN CAVEATS — read before you rely on this in front of anyone:
#   - parser-sidecar speaks gRPC. App Service supports HTTP/2, but this
#     script has NOT been verified end-to-end against it — the
#     `--http20-enabled true` toggle is a starting point, not a
#     guarantee. Test this specific path after deploy; if the .NET gRPC
#     client can't reach it, the extraction pipeline for new corpora
#     will fail (existing signed specs are unaffected).
#   - vb6-sidecar deploys the Wine-based dev image (Linux). No VB6
#     runtime DLLs are mounted, so it comes up with `runtimeReady=false`
#     — matching the platform's own documented degraded state, not a
#     bug. If you have a licensed vb6.exe + DLLs, mount them the same
#     way this script mounts MinIO's Azure Files share.
#   - One shared P1v3 App Service Plan hosts all 13 apps. That's fine
#     for a first deploy, but maven/dotnet/gcc/gfortran/gnucobol/fpc all
#     compile code on demand — under real concurrent load you may need
#     to scale up (P2v3/P3v3) or split the sidecars onto their own plan.
#   - Secrets (Postgres password, MinIO credentials, the Anthropic API
#     key, the property-test callback secret) are set as plain App
#     Service settings here for a first manual deploy. They're encrypted
#     at rest and access-controlled via Azure RBAC, but for anything
#     beyond a sponsorship-subscription trial, move them to Key Vault
#     references instead.
#   - Postgres is opened to "AzureServices" (any Azure-hosted resource),
#     not locked to a VNet. Fine for a first pass; tighten later.
#
# ─────────────────────────────────────────────────────────────────────

set -euo pipefail

# ─── EDIT ME ────────────────────────────────────────────────────────
SUBSCRIPTION="Microsoft Azure Sponsorship"
LOCATION="centralus"

RG="astra-harness-rg"
ACR_NAME="astraharnessacr"          # must be globally unique, alphanumeric only
PLAN="astra-harness-plan"
PLAN_SKU="P1v3"                     # Linux Premium v3, shared by all 13 apps

PG_SERVER="astra-harness-pg"        # must be globally unique
PG_ADMIN_USER="astraadmin"
PG_ADMIN_PASSWORD="CHANGE_ME_a-strong-password-1"
PG_DB="astra"

STORAGE_ACCOUNT="astraharnessstor"  # must be globally unique, lowercase, <=24 chars
FILE_SHARE="minio-data"

MINIO_ROOT_USER="astra"
MINIO_ROOT_PASSWORD="CHANGE_ME_another-strong-password"

PROPERTY_TEST_CALLBACK_SECRET="CHANGE_ME_rotate-this-secret"
ANTHROPIC_API_KEY="PASTE_YOUR_ANTHROPIC_API_KEY_HERE"
ANTHROPIC_MODEL="claude-sonnet-4-5-20250929"
# ────────────────────────────────────────────────────────────────────

APP_PREFIX="astra"
api_url()   { echo "https://${APP_PREFIX}-${1}.azurewebsites.net"; }

# ─── 1. Login + subscription ───────────────────────────────────────
az login
az account set --subscription "$SUBSCRIPTION"

# ─── 2. Resource group ─────────────────────────────────────────────
az group create --name "$RG" --location "$LOCATION"

# ─── 3. Container registry (cloud-side image builds, no local Docker) ──
az acr create --resource-group "$RG" --name "$ACR_NAME" --sku Basic --admin-enabled true
ACR_LOGIN_SERVER=$(az acr show --name "$ACR_NAME" --query loginServer -o tsv)
ACR_USERNAME=$(az acr credential show --name "$ACR_NAME" --query username -o tsv)
ACR_PASSWORD=$(az acr credential show --name "$ACR_NAME" --query "passwords[0].value" -o tsv)

# ─── 4. App Service Plan (Linux, shared by every app below) ───────
az appservice plan create --name "$PLAN" --resource-group "$RG" --is-linux --sku "$PLAN_SKU"

# ─── 5. Managed Postgres (replaces the postgres container) ────────
az postgres flexible-server create \
  --resource-group "$RG" \
  --name "$PG_SERVER" \
  --location "$LOCATION" \
  --admin-user "$PG_ADMIN_USER" \
  --admin-password "$PG_ADMIN_PASSWORD" \
  --sku-name Standard_B2s \
  --tier Burstable \
  --storage-size 32 \
  --version 16 \
  --public-access 0.0.0.0   # Azure's shorthand for "allow Azure-hosted resources"

az postgres flexible-server db create \
  --resource-group "$RG" \
  --server-name "$PG_SERVER" \
  --database-name "$PG_DB"

# uuid-ossp / pg_trgm are restricted extensions on Flexible Server —
# allow-list them before init.sql's CREATE EXTENSION calls can succeed.
az postgres flexible-server parameter set \
  --resource-group "$RG" --server-name "$PG_SERVER" \
  --name azure.extensions --value "uuid-ossp,pg_trgm"

PG_HOST="${PG_SERVER}.postgres.database.azure.com"
PG_CONN_STRING="Host=${PG_HOST};Port=5432;Database=${PG_DB};Username=${PG_ADMIN_USER};Password=${PG_ADMIN_PASSWORD};SSL Mode=Require;Trust Server Certificate=true"

echo "Run infra/postgres/init.sql against the new server (requires the psql client):"
echo "  PGPASSWORD='$PG_ADMIN_PASSWORD' psql \"host=$PG_HOST port=5432 dbname=$PG_DB user=$PG_ADMIN_USER sslmode=require\" -f infra/postgres/init.sql"
echo "No psql locally? Run the same two CREATE EXTENSION lines from Azure Cloud Shell instead."

# ─── 6. Storage account + Azure Files share (MinIO's persistent /data) ──
az storage account create \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RG" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2

az storage share-rm create \
  --resource-group "$RG" \
  --storage-account "$STORAGE_ACCOUNT" \
  --name "$FILE_SHARE" \
  --quota 100

STORAGE_KEY=$(az storage account keys list --resource-group "$RG" --account-name "$STORAGE_ACCOUNT" --query "[0].value" -o tsv)

# ─── helper: create a Web App from an ACR image + point it at the registry ──
deploy_container_app() {
  local name="$1" image="$2" port="$3"
  az webapp create \
    --resource-group "$RG" --plan "$PLAN" --name "${APP_PREFIX}-${name}" \
    --deployment-container-image-name "$image"
  az webapp config container set \
    --resource-group "$RG" --name "${APP_PREFIX}-${name}" \
    --docker-custom-image-name "$image" \
    --docker-registry-server-url "https://${ACR_LOGIN_SERVER}" \
    --docker-registry-server-user "$ACR_USERNAME" \
    --docker-registry-server-password "$ACR_PASSWORD"
  az webapp config appsettings set \
    --resource-group "$RG" --name "${APP_PREFIX}-${name}" \
    --settings WEBSITES_PORT="$port"
}

# ─── 7. The 8 uniform Python/FastAPI sidecars ──────────────────────
# Same shape for all of them: build from ./<name>, push to ACR, deploy,
# route WEBSITES_PORT at each one's own internal port.
declare -A SIDECAR_PORTS=(
  [parser-sidecar]=50051
  [gfortran-sidecar]=51052
  [maven-sidecar]=51053
  [gnucobol-sidecar]=51054
  [fpc-sidecar]=51055
  [gpp-sidecar]=51057
  [csharp-sidecar]=51059
  [property-test-sidecar]=51056
)
for name in "${!SIDECAR_PORTS[@]}"; do
  port="${SIDECAR_PORTS[$name]}"
  az acr build --registry "$ACR_NAME" --image "${name}:latest" "./${name}"
  deploy_container_app "$name" "${ACR_LOGIN_SERVER}/${name}:latest" "$port"
done

# gRPC needs HTTP/2 end-to-end — see the caveat at the top of this file.
az webapp config set --resource-group "$RG" --name "${APP_PREFIX}-parser-sidecar" --http20-enabled true

# ─── 8. vb6-sidecar (different Dockerfile: Wine-based Linux image) ─
az acr build --registry "$ACR_NAME" --image "vb6-sidecar:latest" --file Dockerfile.wine "./vb6-sidecar"
deploy_container_app "vb6-sidecar" "${ACR_LOGIN_SERVER}/vb6-sidecar:latest" 51058

# ─── 9. MinIO (pulled straight from Docker Hub — nothing to build) ─
az webapp create \
  --resource-group "$RG" --plan "$PLAN" --name "${APP_PREFIX}-minio" \
  --deployment-container-image-name "minio/minio:RELEASE.2024-12-18T13-15-44Z"
az webapp config appsettings set \
  --resource-group "$RG" --name "${APP_PREFIX}-minio" \
  --settings \
    WEBSITES_PORT=9000 \
    MINIO_ROOT_USER="$MINIO_ROOT_USER" \
    MINIO_ROOT_PASSWORD="$MINIO_ROOT_PASSWORD"
az webapp config set \
  --resource-group "$RG" --name "${APP_PREFIX}-minio" \
  --startup-file "server /data --console-address :9001"
az webapp config storage-account add \
  --resource-group "$RG" --name "${APP_PREFIX}-minio" \
  --custom-id miniodata --storage-type AzureFiles \
  --account-name "$STORAGE_ACCOUNT" --share-name "$FILE_SHARE" \
  --access-key "$STORAGE_KEY" --mount-path /data

echo "Waiting for MinIO to come up before bootstrapping buckets..."
sleep 45

echo "Bootstrapping MinIO buckets (requires the mc client — https://min.io/docs/minio/linux/reference/minio-mc.html):"
cat <<EOF
  mc alias set astra-minio $(api_url minio) "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"
  mc mb astra-minio/sources
  mc mb astra-minio/signed-specs
  mc mb astra-minio/scaffolds
  mc mb astra-minio/llm-debug-restricted
  mc version enable astra-minio/signed-specs
EOF

# ─── 10. API (.NET 8) ───────────────────────────────────────────────
az acr build --registry "$ACR_NAME" --image "api:latest" --target runtime "./api"
deploy_container_app "api" "${ACR_LOGIN_SERVER}/api:latest" 8080
az webapp config appsettings set \
  --resource-group "$RG" --name "${APP_PREFIX}-api" \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://+:8080" \
    ConnectionStrings__Postgres="$PG_CONN_STRING" \
    Storage__Endpoint="$(api_url minio)" \
    Storage__AccessKey="$MINIO_ROOT_USER" \
    Storage__SecretKey="$MINIO_ROOT_PASSWORD" \
    Storage__Buckets__Sources=sources \
    Storage__Buckets__SignedSpecs=signed-specs \
    Storage__Buckets__Scaffolds=scaffolds \
    Storage__Buckets__LlmDebug=llm-debug-restricted \
    Parser__GrpcEndpoint="$(api_url parser-sidecar)" \
    Validation__GfortranEndpoint="$(api_url gfortran-sidecar)" \
    Validation__MavenEndpoint="$(api_url maven-sidecar)" \
    Validation__GnucobolEndpoint="$(api_url gnucobol-sidecar)" \
    Validation__FpcEndpoint="$(api_url fpc-sidecar)" \
    Validation__GppEndpoint="$(api_url gpp-sidecar)" \
    Validation__Vb6Endpoint="$(api_url vb6-sidecar)" \
    Validation__CsharpEndpoint="$(api_url csharp-sidecar)" \
    Validation__PropertyTestEndpoint="$(api_url property-test-sidecar)" \
    Validation__InternalCallbackBaseUrl="$(api_url api)" \
    Validation__PropertyTestCallbackSecret="$PROPERTY_TEST_CALLBACK_SECRET" \
    Validation__LiveMode4thGate__Enabled=false \
    Auth__DevPersonaBypass=true \
    Auth__DevPersonaDefault=engineer \
    Cors__AllowedOrigins__0="$(api_url frontend)" \
    Database__RecreateOnStartup=false \
    Database__SeedDemo=false \
    Database__SeedMinpack=true \
    Database__SeedLapackBlas=true \
    Database__SeedIndyDemo=true \
    Database__SeedFmtDemo=true \
    Database__SeedVb6Demo=true \
    Database__SeedMvcMusicStoreDemo=true \
    Dev__ResetEnabled=false \
    Llm__Provider=anthropic \
    Llm__ScaffoldProvider=anthropic \
    Llm__Anthropic__ApiKey="$ANTHROPIC_API_KEY" \
    Llm__Anthropic__Model="$ANTHROPIC_MODEL" \
    Llm__Anthropic__BaseUrl="https://api.anthropic.com" \
    Llm__Anthropic__MaxOutputTokens=16384 \
    Docs__Generator__Provider=anthropic

# ─── 11. Worker (.NET 8 + Hangfire) ─────────────────────────────────
az acr build --registry "$ACR_NAME" --image "worker:latest" --target runtime "./worker"
deploy_container_app "worker" "${ACR_LOGIN_SERVER}/worker:latest" 8081
az webapp config appsettings set \
  --resource-group "$RG" --name "${APP_PREFIX}-worker" \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://+:8081" \
    ConnectionStrings__Postgres="$PG_CONN_STRING" \
    Hangfire__DashboardEnabled=true \
    Hangfire__SchedulePingJob=true

# ─── 12. Frontend (React + Vite, built static + nginx) ─────────────
# VITE_API_BASE_URL is baked in at BUILD time (Vite inlines import.meta.env.*
# into the static bundle) — it must point at the real API hostname now,
# not localhost. This repo's frontend/Dockerfile didn't expose that as a
# build ARG until this deploy; if you're on an older checkout, add:
#   ARG VITE_API_BASE_URL
#   ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
# right before `RUN npm run build` in the `build` stage.
az acr build --registry "$ACR_NAME" --image "frontend:latest" --target runtime \
  --build-arg VITE_API_BASE_URL="$(api_url api)" \
  "./frontend"
deploy_container_app "frontend" "${ACR_LOGIN_SERVER}/frontend:latest" 80

echo ""
echo "Done. Frontend: $(api_url frontend)"
echo "API health check: $(api_url api)/health"
echo ""
echo "Remaining manual steps:"
echo "  1. Run infra/postgres/init.sql against \$PG_HOST (command printed above)."
echo "  2. Bootstrap the 4 MinIO buckets with mc (commands printed above)."
echo "  3. Hit $(api_url api)/health and each sidecar's /health to confirm they're up."
echo "  4. Open $(api_url frontend) and walk through an ingest → extract → sign → scaffold cycle."
