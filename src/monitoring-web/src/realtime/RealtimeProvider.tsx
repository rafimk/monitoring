import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { useAuth } from "../auth";
import { config, POLL_MS } from "../config";
import { DEFAULT_RETRY_DELAYS_MS, RealtimeClient, type HubLike } from "./realtimeClient";

const Ctx = createContext<{ connected: boolean }>({ connected: false });

/** Whether live updates are flowing right now (drives the header indicator and the polling fallback). */
export const useLiveConnection = () => useContext(Ctx).connected;

/**
 * Polling interval for a query: none while the live connection is up (events trigger refetches), the slow fallback
 * interval while it is down. Uses the query's own refetchInterval, so there are no extra timers to duplicate.
 */
export function useFallbackPoll(): number | false {
  return useLiveConnection() ? false : POLL_MS;
}

function createSignalR(getToken: () => Promise<string | null>): HubLike {
  return new HubConnectionBuilder()
    // The factory runs for every (re)connect, so each connection attempt carries a fresh Keycloak token.
    .withUrl(config.hubUrl, { accessTokenFactory: async () => (await getToken()) ?? "", withCredentials: false })
    .withAutomaticReconnect({
      // Never give up; back off to 30 s. (The client class also restarts the connection if it is ever closed.)
      nextRetryDelayInMilliseconds: (ctx) => DEFAULT_RETRY_DELAYS_MS[Math.min(ctx.previousRetryCount, DEFAULT_RETRY_DELAYS_MS.length - 1)],
    })
    .configureLogging(LogLevel.Warning)
    .build();
}

export function RealtimeProvider({
  children, createConnection,
}: { children: ReactNode; createConnection?: (getToken: () => Promise<string | null>) => HubLike }) {
  const { status, getToken } = useAuth();
  const qc = useQueryClient();
  const [connected, setConnected] = useState(false);
  const authenticated = status === "authenticated";

  useEffect(() => {
    if (!authenticated) return; // exactly one connection per authenticated session; none otherwise
    const client = new RealtimeClient({
      createConnection: () => (createConnection ?? createSignalR)(getToken),
      invalidate: (keys) => keys.forEach((queryKey) => void qc.invalidateQueries({ queryKey })),
      onConnectionChange: setConnected,
    });
    client.start();
    return () => { void client.stop(); }; // logout / session expiry / unmount
  }, [authenticated, getToken, qc, createConnection]);

  return <Ctx.Provider value={{ connected: authenticated && connected }}>{children}</Ctx.Provider>;
}
