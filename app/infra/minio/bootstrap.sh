#!/bin/sh
# MinIO bucket bootstrap — runs once after MinIO is healthy.
# Idempotent: safe to re-run.

set -eu

MINIO_ALIAS=local

echo "[minio-bootstrap] aliasing MinIO..."
mc alias set "$MINIO_ALIAS" http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null

create_bucket() {
    name="$1"
    if mc ls "$MINIO_ALIAS/$name" >/dev/null 2>&1; then
        echo "[minio-bootstrap] bucket '$name' already exists"
    else
        mc mb "$MINIO_ALIAS/$name"
        echo "[minio-bootstrap] bucket '$name' created"
    fi
}

create_bucket "$MINIO_BUCKET_SOURCES"
create_bucket "$MINIO_BUCKET_SIGNED_SPECS"
create_bucket "$MINIO_BUCKET_SCAFFOLDS"
create_bucket "$MINIO_BUCKET_LLM_DEBUG"

# Apply object-locking + retention to signed-specs (best-effort —
# MinIO supports object lock only when the bucket is created with it.
# In local dev we settle for versioning + a notice; production ships
# Azure Blob immutability instead.)
mc version enable "$MINIO_ALIAS/$MINIO_BUCKET_SIGNED_SPECS" >/dev/null || true

# Restrict the LLM-debug bucket retention via lifecycle (7 days)
cat <<EOF >/tmp/lifecycle.json
{
  "Rules": [
    {
      "ID": "expire-llm-debug",
      "Status": "Enabled",
      "Expiration": { "Days": 7 }
    }
  ]
}
EOF
mc ilm import "$MINIO_ALIAS/$MINIO_BUCKET_LLM_DEBUG" </tmp/lifecycle.json >/dev/null || true

echo "[minio-bootstrap] complete."
