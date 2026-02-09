# TypeScript API Extractor - Minimal Docker image
# Bun-compiled binary on debian-slim for glibc compatibility.
#
# Build:  docker build -f extractors/typescript/Dockerfile -t api-extractor-typescript:latest .
# Size:   ~90MB
#
# Usage:
#   docker run --rm -v /path/to/sdk:/path/to/sdk:ro api-extractor-typescript:latest /path/to/sdk --json

FROM oven/bun:1-debian AS build

WORKDIR /src
COPY src/ApiExtractor.TypeScript/package.json src/ApiExtractor.TypeScript/package-lock.json* ./
COPY src/ApiExtractor.TypeScript/src/ ./src/
RUN bun install && bun run build
RUN bun build --compile --minify ./dist/extract_api.js --outfile ts_extractor

FROM debian:trixie-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    libstdc++6 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/ts_extractor /extractor
ENTRYPOINT ["/extractor"]
