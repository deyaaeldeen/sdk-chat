# Docker Usage

Detailed Docker configuration for SDK Chat.

## Docker Images

| Image | Dockerfile | Size | Purpose |
|-------|------------|------|--------|
| `sdk-chat:latest` | `Dockerfile.release` | ~500MB | Production (Native AOT) |
| `sdk-chat-dev` | `Dockerfile` | ~1.5GB | Development/testing |
| `sdk-chat-demo` | `demo/Dockerfile` | ~2GB | Demo recording (VHS) |

The release image includes:
- .NET Native AOT binary (~40MB)
- Go, Java, Python, TypeScript extractors (glibc-linked)
- GitHub Copilot CLI (~138MB)
- distroless/cc-debian12 base (minimal glibc runtime)

```bash
# Build images
docker build -f Dockerfile.release -t sdk-chat:latest . # Production (default)
docker build -t sdk-chat-dev .                          # Dev
docker build -f demo/Dockerfile -t sdk-chat-demo .      # Demo
```

## Docker Compose

Alternative approach for complex workflows:

```bash
# Generate samples
export GH_TOKEN="ghp_..."
SDK_PATH=/path/to/sdk docker compose run --rm sdk-chat package samples generate /sdk

# Start MCP server with Streamable HTTP on port 8080
docker compose up mcp-http

# Run interactive ACP agent
SDK_PATH=/path/to/sdk docker compose run --rm acp
```

## Advanced Docker Usage

For users who prefer direct Docker commands without wrapper scripts.

### With GitHub Token (Recommended)

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest package samples generate /sdk --count 5
```

### With Copilot Credentials

```bash
docker run --rm \
  -v "$HOME/.copilot:/root/.copilot:ro" \
  -v "/path/to/your-sdk:/sdk" \
  sdk-chat:latest package samples generate /sdk --count 5
```

### With OpenAI

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -e OPENAI_API_KEY="sk-..." \
  sdk-chat:latest package samples generate /sdk --use-openai --count 5
```

### Using a .env File

```bash
docker run --rm \
  -v "/path/to/your-sdk:/sdk" \
  -v "$PWD/.env:/app/.env:ro" \
  sdk-chat:latest package samples generate /sdk --use-openai --load-dotenv
```

### MCP Server (stdio)

```bash
docker run --rm -i \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp
```

### MCP Server (Streamable HTTP)

```bash
docker run --rm -p 8080:8080 \
  -e GH_TOKEN="ghp_..." \
  sdk-chat:latest mcp --transport http --port 8080
```

Connect to `http://localhost:8080/mcp` from your MCP client.

## Rebuilding the Image

```bash
# Force rebuild with --build flag
./scripts/sdk-chat.sh --build package samples generate /path/to/sdk

# Or rebuild manually
docker build -f Dockerfile.release -t sdk-chat:latest .
```
