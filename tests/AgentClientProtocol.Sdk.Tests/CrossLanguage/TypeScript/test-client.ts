// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

/**
 * Simple ACP client using official TypeScript SDK.
 * Used for cross-language interoperability testing with .NET agent.
 */

import {
  ClientSideConnection,
  ndJsonStream,
  PROTOCOL_VERSION,
  type RequestPermissionRequest,
  type RequestPermissionResponse,
  type SessionNotification,
  type Client,
  type Agent,
} from '@agentclientprotocol/sdk';

// Store received updates for verification
const receivedUpdates: SessionNotification[] = [];

// Create client handler factory
const createClient = (_agent: Agent): Client => ({
  async requestPermission(params: RequestPermissionRequest): Promise<RequestPermissionResponse> {
    // Auto-approve all permissions for testing
    return {
      outcome: {
        type: 'selected',
        optionId: 'allow_once',
      },
    };
  },

  async sessionUpdate(params: SessionNotification): Promise<void> {
    // Store updates for verification
    receivedUpdates.push(params);

    // Log to stderr for debugging (stdout is used for protocol)
    console.error(`[Client] Session update received: ${JSON.stringify(params)}`);
  },
});

async function main(): Promise<void> {
  // Create stream from stdin/stdout
  const stream = ndJsonStream(process.stdin, process.stdout);

  // Create connection (implements Agent interface for sending requests)
  const connection = new ClientSideConnection(createClient, stream);

  try {
    // Initialize
    const initResponse = await connection.initialize({
      protocolVersion: PROTOCOL_VERSION,
      clientCapabilities: {},
    });
    console.error(`[Client] Initialized: ${JSON.stringify(initResponse)}`);

    // Create session
    const sessionResponse = await connection.newSession({
      cwd: '/test',
      mcpServers: [],
    });
    console.error(`[Client] Session created: ${sessionResponse.sessionId}`);

    // Send prompt
    const promptResponse = await connection.prompt({
      sessionId: sessionResponse.sessionId,
      prompt: [
        {
          type: 'text',
          text: 'Hello from TypeScript client',
        },
      ],
    });
    console.error(`[Client] Prompt response: ${JSON.stringify(promptResponse)}`);

    // Verify stop reason
    if (promptResponse.stopReason === 'endTurn') {
      console.error('[Client] SUCCESS: Received expected stop reason');
      process.exit(0);
    } else {
      console.error(`[Client] ERROR: Unexpected stop reason: ${promptResponse.stopReason}`);
      process.exit(1);
    }
  } catch (error) {
    const err = error as Error;
    console.error(`[Client] ERROR: ${err.message}`);
    console.error(err.stack);
    process.exit(1);
  }
}

main().catch((err: Error) => {
  console.error(`[Client] Fatal error: ${err.message}`);
  console.error(err.stack);
  process.exit(1);
});
