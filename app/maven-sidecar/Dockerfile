# syntax=docker/dockerfile:1.6
# Astra maven sidecar — Java 21 (Eclipse Temurin) + Maven 3.9 + FastAPI.
#
# Mirrors the gfortran-sidecar's REST surface so the API's validator
# can drive `mvn compile` and `mvn test` against the generated Java
# Spring scaffolds (Phase 5.4 archetype output) and assert build +
# test outcomes.
FROM eclipse-temurin:21-jdk-jammy AS base

WORKDIR /app

# Maven + Python for the HTTP shell. Pre-cache the Maven plugins that
# every project we drive will need (compiler, surefire, jar) so the
# first POST /compile inside a fresh container doesn't pay a 60-second
# online resolve.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        python3 \
        python3-pip \
        python3-venv \
 && curl -sSL https://archive.apache.org/dist/maven/maven-3/3.9.6/binaries/apache-maven-3.9.6-bin.tar.gz \
      | tar -xz -C /opt \
 && ln -s /opt/apache-maven-3.9.6/bin/mvn /usr/local/bin/mvn \
 && rm -rf /var/lib/apt/lists/*

# Python virtualenv for the FastAPI shell.
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

# Pre-warm the local Maven repository against a throw-away POM so the
# heavy plugins land at build time, not at first user request.
# This bakes the offline cache into the image (~150 MB) so the
# container is reproducible across hosts that have no internet at run
# time (a SOC2 deployment constraint we've already adopted for the
# gfortran sidecar's apt mirror).
COPY warmup/ /warmup/
RUN cd /warmup && mvn -B -q \
        org.apache.maven.plugins:maven-compiler-plugin:3.13.0:help \
        org.apache.maven.plugins:maven-surefire-plugin:3.2.5:help \
        org.apache.maven.plugins:maven-jar-plugin:3.4.1:help \
        dependency:resolve-plugins \
        dependency:resolve \
        -Dsilent=true \
    && rm -rf /warmup

COPY maven_sidecar/ maven_sidecar/

ENV PYTHONUNBUFFERED=1 \
    MAVEN_PORT=51053 \
    MAVEN_WORKDIR=/var/tmp/maven-runs \
    MAVEN_OPTS="-Xmx512m -Dfile.encoding=UTF-8"

EXPOSE 51053

CMD ["python3", "-m", "maven_sidecar.server"]
