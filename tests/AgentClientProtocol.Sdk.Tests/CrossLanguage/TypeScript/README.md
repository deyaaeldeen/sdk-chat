# TypeScript ACP Test Fixtures

Test fixtures using the official [@agentclientprotocol/sdk](https://www.npmjs.com/package/@agentclientprotocol/sdk) for cross-language interoperability testing.

## Setup

```bash
npm install
```

## Files

- `test-agent.js` — Simple ACP agent that echoes prompts back
- `test-client.js` — Simple ACP client that sends test prompts

## Usage

These are invoked by the .NET test suite via `CrossLanguageInteropTests.cs`.

### Test Agent

```bash
node test-agent.js
```

Runs an ACP agent on stdin/stdout that:
- Responds to `initialize` with agent info
- Creates sessions via `session/new`
- Echoes prompts back with `session/update` notifications

### Test Client

```bash
node test-client.js
```

Runs an ACP client on stdin/stdout that:
- Sends `initialize` request
- Creates a session
- Sends a test prompt
- Verifies the response

## Protocol Version

These fixtures use ACP protocol version 1 (2024-11-05), matching the .NET SDK implementation.
