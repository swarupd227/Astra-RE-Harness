FROM python:3.12-slim AS base

WORKDIR /app

# System deps for grpcio (minimal — wheels are usually available)
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Install Python deps
COPY pyproject.toml ./
RUN pip install --no-cache-dir \
        "grpcio>=1.66.0" \
        "grpcio-tools>=1.66.0" \
        "grpcio-health-checking>=1.66.0" \
        "protobuf>=5.27.0" \
        "fparser>=0.1.4"

# Copy proto + source
COPY proto/ proto/
COPY parser_sidecar/ parser_sidecar/

# Generate Python stubs from the proto (builds parser_pb2.py + parser_pb2_grpc.py
# inside the package directory).
RUN python -m grpc_tools.protoc \
        -I=proto \
        --python_out=parser_sidecar \
        --grpc_python_out=parser_sidecar \
        proto/parser.proto

# Fix the relative import in the generated _grpc.py module so it works
# when the package is imported as parser_sidecar.parser_pb2_grpc.
RUN sed -i 's/^import parser_pb2 as parser__pb2$/from . import parser_pb2 as parser__pb2/' \
        parser_sidecar/parser_pb2_grpc.py

ENV PYTHONUNBUFFERED=1 \
    PARSER_GRPC_PORT=50051

EXPOSE 50051
CMD ["python", "-m", "parser_sidecar.server"]
