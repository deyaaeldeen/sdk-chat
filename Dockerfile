# SDK Chat Development Container
# Used for: local development, CI, demo recording
#
# Build:  docker build -t sdk-chat-dev .
# Run:    docker run --rm -v $(pwd):/workspace -w /workspace sdk-chat-dev dotnet build
# Shell:  docker run --rm -it -v $(pwd):/workspace -w /workspace sdk-chat-dev bash

FROM mcr.microsoft.com/dotnet/sdk:10.0

LABEL org.opencontainers.image.source="https://github.com/deyaaeldeen/sdk-chat"
LABEL org.opencontainers.image.description="SDK Chat development environment"

# Install all language runtimes for API extractors + dev tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core tools
    curl git unzip \
    # Python extractor
    python3 python3-pip \
    # TypeScript/JavaScript extractor
    nodejs npm \
    # Go extractor
    golang-go \
    # VHS demo recording dependencies
    ffmpeg ttyd fonts-liberation \
    libasound2t64 libatk-bridge2.0-0 libatk1.0-0 libcups2 libdbus-1-3 \
    libdrm2 libgbm1 libgtk-3-0 libnspr4 libnss3 libxcomposite1 \
    libxdamage1 libxfixes3 libxkbcommon0 libxrandr2 xdg-utils \
    && rm -rf /var/lib/apt/lists/*

# Install Chrome for VHS headless rendering
RUN curl -fsSL https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb -o /tmp/chrome.deb \
    && (dpkg -i /tmp/chrome.deb || apt-get install -fy) \
    && rm -rf /tmp/chrome.deb

# Install JBang for Java extractor
RUN curl -Ls https://sh.jbang.dev | bash -s - app setup
ENV PATH="$PATH:/root/.jbang/bin"

# Install VHS for demo recording
RUN curl -fsSL https://github.com/charmbracelet/vhs/releases/download/v0.10.0/vhs_0.10.0_Linux_x86_64.tar.gz \
    | tar -xz --strip-components=1 -C /usr/local/bin vhs_0.10.0_Linux_x86_64/vhs

# Install GitHub Copilot CLI
RUN curl -fsSL https://gh.io/copilot-install | bash

# Clone demo SDK (for demo recording)
RUN git clone --depth 1 https://github.com/openai/openai-dotnet /root/openai-dotnet

# Environment
ENV VHS_NO_SANDBOX=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

WORKDIR /workspace

# Default: run tests
CMD ["dotnet", "test"]
