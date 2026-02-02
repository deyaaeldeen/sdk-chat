# SDK Chat Demo Recording Container
# Used for: VHS demo recording with headless Chrome
#
# Build:  docker build -f demo/Dockerfile -t sdk-chat-demo .
# Record: docker run --rm -v $(pwd):/workspace -w /workspace sdk-chat-demo vhs demo/demo.tape

FROM mcr.microsoft.com/dotnet/sdk:10.0

LABEL org.opencontainers.image.source="https://github.com/deyaaeldeen/sdk-chat"
LABEL org.opencontainers.image.description="SDK Chat demo recording environment"

# Install language runtimes + demo recording dependencies
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
ENV JBANG_DIR=/opt/jbang
RUN curl -Ls https://sh.jbang.dev | bash -s - app setup && \
    chmod -R a+rx /opt/jbang && \
    mkdir -p /opt/jbang/cache && \
    chmod -R a+rwx /opt/jbang/cache
ENV PATH="$PATH:/opt/jbang/bin"

# Install GitHub Copilot CLI
RUN curl -fsSL https://gh.io/copilot-install | bash

# Install VHS
RUN curl -fsSL https://github.com/charmbracelet/vhs/releases/download/v0.10.0/vhs_0.10.0_Linux_x86_64.tar.gz \
    | tar -xz --strip-components=1 -C /usr/local/bin vhs_0.10.0_Linux_x86_64/vhs

# Clone demo SDK
RUN git clone --depth 1 https://github.com/openai/openai-dotnet /root/openai-dotnet

# Environment
ENV VHS_NO_SANDBOX=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

WORKDIR /workspace

# VHS needs root for Chrome sandbox
# Default: record demo
CMD ["vhs", "demo/demo.tape"]
