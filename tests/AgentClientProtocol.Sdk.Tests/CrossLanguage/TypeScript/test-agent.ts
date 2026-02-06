// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

/**
 * Simple ACP agent using official TypeScript SDK.
 * Used for cross-language interoperability testing with .NET client.
 */

import {
  AgentSideConnection,
  ndJsonStream,
  PROTOCOL_VERSION,
  type InitializeRequest,
  type InitializeResponse,
  type NewSessionRequest,
  type NewSessionResponse,
  type PromptRequest,
  type PromptResponse,
  type CancelNotification,
  type Agent,
} from '@agentclientprotocol/sdk';

// Create agent handler factory
const createAgent = (conn: AgentSideConnection): Agent => ({
  async initialize(params: InitializeRequest): Promise<InitializeResponse> {
    return {
      protocolVersion: PROTOCOL_VERSION,
      agentCapabilities: {},
      agentInfo: {
        name: 'typescript-test-agent',
        version: '1.0.0',
      },
    };
  },

  async newSession(params: NewSessionRequest): Promise<NewSessionResponse> {
    return {
      sessionId: `ts-session-${Date.now()}`,
    };
  },

  async prompt(params: PromptRequest): Promise<PromptResponse> {
    // Echo the prompt back as a text content response
    const promptText = params.prompt
      .filter((c): c is { type: 'text'; text: string } => c.type === 'text')
      .map((c) => c.text)
      .join(' ');

    // Send session update with response
    await conn.sessionUpdate({
      sessionId: params.sessionId,
      updates: [
        {
          type: 'response',
          content: [
            {
              type: 'text',
              text: `Echo from TypeScript: ${promptText}`,
            },
          ],
        },
      ],
    });

    return {
      stopReason: 'endTurn',
    };
  },

  async cancel(params: CancelNotification): Promise<void> {
    // No-op for test agent
  },
});

// Create stream from stdin/stdout
const stream = ndJsonStream(process.stdin, process.stdout);

// Create connection
const connection = new AgentSideConnection(createAgent, stream);

// Connection will close when stdin closes
await connection.closed;
