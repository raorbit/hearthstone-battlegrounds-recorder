import { createMockRpcClient } from "./mockBridge";
import type {
  RpcArgs,
  RpcClient,
  RpcMethod,
  RpcMethodMap,
  RpcNotification,
  RpcNotificationMap,
} from "./types";

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
  removeEventListener(type: "message", listener: (event: MessageEvent<unknown>) => void): void;
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewBridge;
    };
  }
}

interface JsonRpcRequest {
  jsonrpc: "2.0";
  id: string;
  method: RpcMethod;
  params?: unknown;
}

interface JsonRpcResponse {
  jsonrpc?: "2.0";
  id: string | number;
  result?: unknown;
  error?: {
    code?: number;
    message?: string;
    data?: unknown;
  };
}

interface JsonRpcNotification {
  jsonrpc?: "2.0";
  method: RpcNotification;
  params?: unknown;
}

interface PendingRequest {
  resolve(value: unknown): void;
  reject(reason: Error): void;
  timer: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function parseMessage(data: unknown): unknown {
  if (typeof data !== "string") {
    return data;
  }

  try {
    return JSON.parse(data) as unknown;
  } catch {
    return null;
  }
}

export class RpcError extends Error {
  constructor(
    message: string,
    readonly code?: number,
    readonly data?: unknown,
  ) {
    super(message);
    this.name = "RpcError";
  }
}

class NativeRpcClient implements RpcClient {
  readonly mode = "native" as const;
  private sequence = 0;
  private readonly pending = new Map<string, PendingRequest>();
  private readonly listeners = new Map<RpcNotification, Set<(params: never) => void>>();

  constructor(private readonly webview: WebViewBridge) {
    this.webview.addEventListener("message", this.handleMessage);
  }

  request<M extends RpcMethod>(
    method: M,
    ...args: RpcArgs<M>
  ): Promise<RpcMethodMap[M]["result"]> {
    const id = `web-${Date.now().toString(36)}-${(++this.sequence).toString(36)}`;
    const params = (args as unknown[])[0] as RpcMethodMap[M]["params"];
    const request: JsonRpcRequest = {
      jsonrpc: "2.0",
      id,
      method,
      ...(params === undefined ? {} : { params }),
    };

    return new Promise<RpcMethodMap[M]["result"]>((resolve, reject) => {
      // A manual stop includes recorder finalization and may legitimately take up to 90 seconds.
      const timeoutMs = method === "recorder.stop" ? 120_000 : 15_000;
      const timer = window.setTimeout(() => {
        this.pending.delete(id);
        reject(new RpcError(`The ${method} request timed out.`));
      }, timeoutMs);

      this.pending.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
        timer,
      });

      try {
        this.webview.postMessage(request);
      } catch (error) {
        window.clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  on<N extends RpcNotification>(
    method: N,
    handler: (params: RpcNotificationMap[N]) => void,
  ): () => void {
    let listeners = this.listeners.get(method);
    if (!listeners) {
      listeners = new Set();
      this.listeners.set(method, listeners);
    }
    listeners.add(handler as (params: never) => void);
    return () => listeners?.delete(handler as (params: never) => void);
  }

  private readonly handleMessage = (event: MessageEvent<unknown>): void => {
    const message = parseMessage(event.data);
    if (!isRecord(message)) {
      return;
    }

    if ("id" in message) {
      this.handleResponse(message as unknown as JsonRpcResponse);
      return;
    }

    if (typeof message.method === "string") {
      this.handleNotification(message as unknown as JsonRpcNotification);
    }
  };

  private handleResponse(response: JsonRpcResponse): void {
    const id = String(response.id);
    const pending = this.pending.get(id);
    if (!pending) {
      return;
    }

    window.clearTimeout(pending.timer);
    this.pending.delete(id);

    if (response.error) {
      pending.reject(new RpcError(
        response.error.message ?? "The native host rejected the request.",
        response.error.code,
        response.error.data,
      ));
      return;
    }

    pending.resolve(response.result);
  }

  private handleNotification(notification: JsonRpcNotification): void {
    const listeners = this.listeners.get(notification.method);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(notification.params as never);
    }
  }
}

const nativeWebView = window.chrome?.webview;

export const bridge: RpcClient = nativeWebView
  ? new NativeRpcClient(nativeWebView)
  : createMockRpcClient();
