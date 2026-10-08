// Client-side logger: everything goes to the browser console as before, and info and above is
// also batched up to the server's /api/client-logs, which stores it in the Logs table
// (SourceContext = 'ChitChat.Client') next to the server's own logs.

type LogLevel = 'debug' | 'info' | 'warn' | 'error';

interface LogEntry {
    level: LogLevel;
    message: string;
    timestamp: string;
    userName: string | null;
    url: string;
    userAgent: string;
    stack?: string;
}

const ENDPOINT = `${import.meta.env.BASE_URL}api/client-logs`;
const FLUSH_INTERVAL_MS = 5_000;
const MAX_BATCH = 50; // server-side cap per request
const MAX_BUFFERED = 200; // oldest entries are dropped beyond this, e.g. while the server is down

let buffer: LogEntry[] = [];
let userName: string | null = null;
let flushTimer: ReturnType<typeof setTimeout> | undefined;

function describe(error: unknown): { text: string; stack?: string } {
  if (error instanceof Error) {
      return { text: `${error.name}: ${error.message}`, stack: error.stack };
  }
    return { text: typeof error === 'string' ? error : JSON.stringify(error) };
}

function enqueue(level: LogLevel, message: string, error?: unknown) {
    const detail = error === undefined ? undefined : describe(error);
    buffer.push({
        level,
        message: detail ? `${message} ${detail.text}` : message,
        timestamp: new Date().toISOString(),
        userName,
        url: window.location.href,
        userAgent: navigator.userAgent,
        stack: detail?.stack,
    });
  if (buffer.length > MAX_BUFFERED) {
      buffer = buffer.slice(-MAX_BUFFERED);
  }

  // Errors go out right away so they aren't lost if the tab closes; the rest is batched.
  if (level === 'error') {
    void flush();
  } else if (flushTimer === undefined) {
    flushTimer = setTimeout(() => void flush(), FLUSH_INTERVAL_MS);
  }
}

async function flush() {
  clearTimeout(flushTimer);
  flushTimer = undefined;

  while (buffer.length > 0) {
    const batch = buffer.slice(0, MAX_BATCH);
    buffer = buffer.slice(MAX_BATCH);
    try {
      const response = await fetch(ENDPOINT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(batch),
        keepalive: true,
      })
      if (!response.ok) {
        // Rejected (e.g. oversized) -- retrying the same batch would fail the same way.
          console.warn(`Client log upload rejected: HTTP ${response.status}`);
      }
    } catch {
      // Server unreachable: put the batch back and let the next log or flush retry it. Never
      // log this through the logger itself, or a dead server would feed an endless loop.
        buffer = [...batch, ...buffer].slice(-MAX_BUFFERED);
        return;
    }
  }
}

// sendBeacon survives the page being closed or backgrounded, where a fetch may be cancelled.
function flushOnExit() {
  if (buffer.length === 0 || typeof navigator.sendBeacon !== 'function') {
      return;
  }
    const batch = buffer.slice(0, MAX_BATCH);
    const blob = new Blob([JSON.stringify(batch)], { type: 'application/json' });
  if (navigator.sendBeacon(ENDPOINT, blob)) {
      buffer = buffer.slice(MAX_BATCH);
  }
}

export const logger = {
  debug(message: string) {
        console.debug(message);
  },
  info(message: string) {
      console.info(message);
      enqueue('info', message);
  },
  warn(message: string, error?: unknown) {
      console.warn(message, ...(error === undefined ? [] : [error]));
      enqueue('warn', message, error);
  },
  error(message: string, error?: unknown) {
      console.error(message, ...(error === undefined ? [] : [error]));
    enqueue('error', message, error)
  },
  // Attached to every later entry, so the server can tell whose browser a log came from.
  setUserName(name: string | null) {
      userName = name;
  },
}

let globalHandlersInstalled = false;

export function installGlobalErrorHandlers() {
  if (globalHandlersInstalled) {
      return;
  }
    globalHandlersInstalled = true;

  window.addEventListener('error', (event) => {
      logger.error(`Uncaught error at ${event.filename}:${event.lineno}:${event.colno}:`, event.error ?? event.message);
  });
  window.addEventListener('unhandledrejection', (event) => {
      logger.error('Unhandled promise rejection:', event.reason);
  });
    window.addEventListener('pagehide', flushOnExit);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') {
        flushOnExit();
    }
  });
}
