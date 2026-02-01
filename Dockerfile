# SDK Chat Development Container
# Used for: local development, CI, testing
#
# Build:  docker build -t sdk-chat-dev .
# Run:    docker run --rm -u $(id -u):$(id -g) -v $(pwd):/workspace sdk-chat-dev dotnet build
# Shell:  docker run --rm -it -u $(id -u):$(id -g) -v $(pwd):/workspace sdk-chat-dev bash
# Test:   docker run --rm -u $(id -u):$(id -g) -v $(pwd):/workspace sdk-chat-dev
#
# For production: use Dockerfile.release
# For demo recording: use Dockerfile.demo

FROM mcr.microsoft.com/dotnet/sdk:10.0

LABEL org.opencontainers.image.source="https://github.com/deyaaeldeen/sdk-chat"
LABEL org.opencontainers.image.description="SDK Chat development environment"

# Install language runtimes for API extractors
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core tools
    curl git unzip \
    # Python extractor
    python3 python3-pip \
    # TypeScript/JavaScript extractor
    nodejs npm \
    # Go extractor
    golang-go \
    && rm -rf /var/lib/apt/lists/*

# Install JBang for Java extractor
ENV JBANG_DIR=/opt/jbang
RUN curl -Ls https://sh.jbang.dev | bash -s - app setup && \
    chmod -R a+rx /opt/jbang && \
    mkdir -p /opt/jbang/cache && \
    chmod -R a+rwx /opt/jbang/cache
ENV PATH="$PATH:/opt/jbang/bin"

# Install GitHub Copilot CLI
RUN curl -fsSL https://gh.io/copilot-install | bash

# Security: Create non-root user
RUN groupadd --gid 1001 sdkchat && \
    useradd --uid 1001 --gid sdkchat --shell /bin/bash --create-home sdkchat && \
    mkdir -p /workspace && \
    chown -R sdkchat:sdkchat /workspace

# Environment
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

WORKDIR /workspace

# Switch to non-root user
USER sdkchat

# Default: run tests
CMD ["dotnet", "test"]
