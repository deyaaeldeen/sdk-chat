# SDK Chat Development Container
# Used for: local development, CI, testing
#
# Requires base image:
#   docker build -f Dockerfile.base -t sdk-chat-base .
#
# Build:  docker build -t sdk-chat-dev .
# Run:    docker run --rm -v $(pwd):/workspace -w /workspace sdk-chat-dev dotnet build
# Shell:  docker run --rm -it -v $(pwd):/workspace -w /workspace sdk-chat-dev bash
# Test:   docker run --rm -v $(pwd):/workspace -w /workspace sdk-chat-dev
#
# For production: use Dockerfile.release
# For demo recording: use Dockerfile.demo

FROM sdk-chat-base

LABEL org.opencontainers.image.description="SDK Chat development environment"

# Switch to non-root user
USER sdkchat

# Default: run tests
CMD ["dotnet", "test"]
