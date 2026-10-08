import { useCallback, useEffect, useRef, useState } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { logger } from './logger'

export interface ChatMessage {
    id: number;
  userName: string;
  message: string;
  sentAt: string;
  audioContentType?: string | null;
  photoContentType?: string | null;
}

export interface PrivateMessage {
    id: number;
    fromUserName: string;
    toUserName: string;
    message: string;
    sentAt: string;
    audioContentType?: string | null;
	photoContentType?: string | null;
}

export type OutgoingMessage =
    | { kind: 'text'; text: string }
    | { kind: 'audio'; blob: Blob }
    | { kind: 'photo'; blob: Blob };

// Shape of the hub's SendMessageRequest.
interface SendMessageRequest {
    kind: OutgoingMessage['kind'];
    toUserName?: string;
    text?: string;
    dataBase64?: string;
    contentType?: string;
}

async function blobToBase64(blob: Blob): Promise<string> {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let binary = '';
    // chunked to avoid blowing the call stack on String.fromCharCode(...bytes) for large files
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
    }
    return btoa(binary);
}

async function toRequest(message: OutgoingMessage, toUserName?: string): Promise<SendMessageRequest> {
    if (message.kind === 'text') {
        return { kind: 'text', toUserName, text: message.text };
    }
    // The JSON hub protocol has no binary type, so media goes as base64 text.
    return { kind: message.kind, toUserName, dataBase64: await blobToBase64(message.blob), contentType: message.blob.type };
}

// System messages ("X joined") are synthesized client-side, never stored -- give them a
// negative id so they can never collide with a real row's (positive, autoincrement) id.
// Backoff for reconnecting: quick at first, then every 15s indefinitely.
const RECONNECT_DELAYS_MS = [0, 2_000, 5_000, 10_000, 15_000];

let systemMessageId = -1;
function nextSystemMessageId(): number {
    return systemMessageId--;
}

function partnerOf(msg: PrivateMessage, userName: string): string {
    return msg.fromUserName === userName ? msg.toUserName : msg.fromUserName;
}

function groupByPartner(history: PrivateMessage[], userName: string): Record<string, PrivateMessage[]> {
  const grouped: Record<string, PrivateMessage[]> = {}
  for (const msg of history) {
      const partner = partnerOf(msg, userName);
      grouped[partner] = [...(grouped[partner] ?? []), msg];
  }
    return grouped;
}

export function useChat(userName: string | null) {
    const connectionRef = useRef<HubConnection | null>(null);
    const [messages, setMessages] = useState<ChatMessage[]>([]);
    const [privateMessages, setPrivateMessages] = useState<Record<string, PrivateMessage[]>>({});
    const [onlineUsers, setOnlineUsers] = useState<string[]>([]);
    const [isConnected, setIsConnected] = useState(false);

  useEffect(() => {
    logger.setUserName(userName);
    if (!userName) {
        return;
    }

      const connection = new HubConnectionBuilder()
          .withUrl(`${import.meta.env.BASE_URL}chatHub`)
          // Never give up: the default policy stops after 4 attempts (~40s) and leaves the tab
          // dead until a manual reload, which is what long phone sleeps or server restarts hit.
          .withAutomaticReconnect({
              nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
                  RECONNECT_DELAYS_MS[Math.min(previousRetryCount, RECONNECT_DELAYS_MS.length - 1)],
          })
          .configureLogging(LogLevel.Warning)
          .build();

    connection.on('MessageHistory', (history: ChatMessage[]) => {
        setMessages(history);
    })

    connection.on('ReceiveMessage', (incoming: ChatMessage) => {
        setMessages((previous) => [...previous, incoming]);
    })

    connection.on('PrivateMessageHistory', (history: PrivateMessage[]) => {
        setPrivateMessages(groupByPartner(history, userName));
    })

    connection.on('ReceivePrivateMessage', (incoming: PrivateMessage) => {
        const partner = partnerOf(incoming, userName);
      setPrivateMessages((previous) => ({
        ...previous,
        [partner]: [...(previous[partner] ?? []), incoming],
      }))
    })

      connection.on('UserJoined', (joinedUser: string) => {
          setMessages((previous) => [
              ...previous,
              {
                  id: nextSystemMessageId(),
                  userName: 'system',
                  message: `${joinedUser} joined the chat`,
                  sentAt: new Date().toISOString(),
              },
          ])
      });

      connection.on('UserLeft', (leftUser: string) => {
          setMessages((previous) => [
              ...previous,
              {
                  id: nextSystemMessageId(),
                  userName: 'system',
                  message: `${leftUser} left the chat`,
                  sentAt: new Date().toISOString(),
              },
          ])
      });

    connection.on('OnlineUsers', (users: string[]) => {
        setOnlineUsers(users);
    });

    // A reconnect gets a new connection id the server has no Join record for, so every send
    // would fail with "Join the chat before sending messages" -- re-join before re-enabling input.
    connection.onreconnecting((error) => {
        setIsConnected(false);
        logger.warn('SignalR connection lost, reconnecting.', error);
    })
      connection.onreconnected(() =>
          connection
              .invoke('Join', userName)
              .then(() => {
                  setIsConnected(true);
                  logger.info('SignalR reconnected and re-joined.');
              })
              .catch((error) => {
                  // Connected but not joined is useless -- drop it so onclose starts over cleanly.
                  logger.error('SignalR re-join failed:', error);
                  connection.stop();
              }),
      );

    // The automatic-reconnect policy only covers drops after a successful start; the first
    // start (or a stop after a failed re-join) still needs its own retry loop.
      let disposed = false;
      let retryTimer: ReturnType<typeof setTimeout> | undefined;
      let startAttempts = 0;

    const connect = () => {
      connection
        .start()
        .then(() => connection.invoke('Join', userName))
        .then(() => {
            if (startAttempts > 0) {
                logger.info(`SignalR connected after ${startAttempts} failed attempt(s).`);
            }
            startAttempts = 0;
            setIsConnected(true);
        })
        .catch((error) => {
          // A warning, not an error: it's retried indefinitely, and while the server is down
          // these can't be uploaded anyway -- they go up in one batch once it's back.
            logger.warn('SignalR connection failed:', error);
          if (connection.state === HubConnectionState.Connected) {
              connection.stop(); // onclose schedules the retry
          } else {
              scheduleConnect();
          }
        })
    }

    const scheduleConnect = () => {
      if (disposed) {
          return;
      }
      const delay = RECONNECT_DELAYS_MS[Math.min(startAttempts++, RECONNECT_DELAYS_MS.length - 1)]; 
        retryTimer = setTimeout(connect, delay);
    }

    connection.onclose((error) => {
        setIsConnected(false);
        if (error && !disposed) {
            logger.warn('SignalR connection closed.', error);
        }
        scheduleConnect();
    })

      connect();

    connectionRef.current = connection;

    return () => {
      disposed = true;
      clearTimeout(retryTimer);
      connection.stop();
      connectionRef.current = null;
      setIsConnected(false);
      setMessages([]);
      setPrivateMessages({});
      setOnlineUsers([]);
    }
  }, [userName]);

    // One send for every kind of message. No `toUserName` = lobby, otherwise a private message.
    const sendMessage = useCallback(async (message: OutgoingMessage, toUserName?: string) => {
        const connection = connectionRef.current;
        if (!connection || connection.state !== HubConnectionState.Connected) {
            return;
        }

        try {
            await connection.invoke('SendMessage', await toRequest(message, toUserName));
        } catch (error) {
            logger.error(`Sending ${message.kind} message failed:`, error);
        }
    }, []);

    return {
        messages,
        privateMessages,
        onlineUsers,
        isConnected,
        sendMessage,
    };
}
