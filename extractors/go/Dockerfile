# Go API Extractor - Minimal Docker image
# Fully static binary, no runtime dependencies.
#
# Build:  docker build -f extractors/go/Dockerfile -t api-extractor-go:latest .
# Size:   ~5MB
#
# Usage:
#   docker run --rm -v /path/to/sdk:/path/to/sdk:ro api-extractor-go:latest /path/to/sdk --json

FROM golang:1.22-alpine AS build

WORKDIR /src
COPY src/ApiExtractor.Go/extract_api.go .
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build \
    -ldflags='-s -w' \
    -o /go_extractor extract_api.go

FROM scratch
COPY --from=build /go_extractor /extractor
ENTRYPOINT ["/extractor"]
