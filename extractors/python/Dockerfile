# Python API Extractor - Minimal Docker image
# PyInstaller binary on debian-slim for glibc compatibility.
#
# Build:  docker build -f extractors/python/Dockerfile -t api-extractor-python:latest .
# Size:   ~80MB
#
# Usage:
#   docker run --rm -v /path/to/sdk:/path/to/sdk:ro api-extractor-python:latest /path/to/sdk --json

FROM debian:trixie-slim AS build

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv python3-dev binutils \
    && pip3 install --break-system-packages --no-cache-dir pyinstaller \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY src/ApiExtractor.Python/extract_api.py .
RUN pyinstaller --onefile --strip --name python_extractor extract_api.py

FROM debian:trixie-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    libstdc++6 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/dist/python_extractor /extractor
ENTRYPOINT ["/extractor"]
