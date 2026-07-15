import type {
  CoordinatorState,
  Marker,
  MatchSummary,
  RpcArgs,
  RpcClient,
  RpcMethod,
  RpcMethodMap,
  RpcNotification,
  RpcNotificationMap,
} from "./types";

const now = Date.now();
const hour = 60 * 60 * 1_000;
const day = 24 * hour;

const mockMatches: MatchSummary[] = [
  {
    id: 101,
    startedAt: new Date(now - 2 * hour).toISOString(),
    gameType: "solo",
    heroCardId: "TB_BaconShop_HERO_41",
    place: 1,
    tavernTurns: 17,
    videoStatus: "complete",
    videoSizeBytes: 1_482_334_208,
    videoDurationMs: 24 * 60 * 1_000 + 55_000,
    starred: true,
    manualRating: 6_412,
    mediaUrl: null,
  },
  {
    id: 102,
    startedAt: new Date(now - 5 * hour).toISOString(),
    gameType: "solo",
    heroCardId: "TB_BaconShop_HERO_16",
    place: 5,
    tavernTurns: 14,
    videoStatus: "complete",
    videoSizeBytes: 1_136_885_760,
    videoDurationMs: 20 * 60 * 1_000 + 10_000,
    starred: false,
    manualRating: null,
    mediaUrl: null,
  },
  {
    id: 103,
    startedAt: new Date(now - day - 3 * hour).toISOString(),
    gameType: "duos",
    heroCardId: "BG27_HERO_801",
    place: 2,
    tavernTurns: 15,
    videoStatus: "incomplete",
    videoSizeBytes: 846_393_344,
    videoDurationMs: 16 * 60 * 1_000 + 18_000,
    starred: false,
    manualRating: 5_944,
    mediaUrl: null,
  },
  {
    id: 104,
    startedAt: new Date(now - 10 * day).toISOString(),
    gameType: "solo",
    heroCardId: "TB_BaconShop_HERO_59",
    place: 3,
    tavernTurns: 16,
    videoStatus: "complete",
    videoSizeBytes: 1_294_336_000,
    videoDurationMs: 23 * 60 * 1_000 + 2_000,
    starred: false,
    manualRating: 6_220,
    mediaUrl: null,
  },
  {
    id: 105,
    startedAt: new Date(now - 46 * day).toISOString(),
    gameType: "duos",
    heroCardId: "BG30_HERO_304",
    place: 4,
    tavernTurns: 13,
    videoStatus: "missing",
    videoSizeBytes: null,
    videoDurationMs: null,
    starred: true,
    manualRating: null,
    mediaUrl: null,
  },
  {
    id: 106,
    startedAt: new Date(now - 18 * day).toISOString(),
    gameType: "notBattlegrounds",
    heroCardId: null,
    place: null,
    tavernTurns: 0,
    videoStatus: "incomplete",
    videoSizeBytes: 612_368_384,
    videoDurationMs: 12 * 60 * 1_000 + 31_000,
    starred: false,
    manualRating: null,
    mediaUrl: null,
  },
];

function markersFor(match: MatchSummary): Marker[] {
  const duration = match.videoDurationMs ?? 1;
  const turnStep = duration / Math.max(match.tavernTurns, 1);
  const markers: Marker[] = [];

  for (let turn = 1; turn <= match.tavernTurns; turn += 1) {
    const atMs = Math.round(Math.max(0, (turn - 1) * turnStep));
    markers.push({ kind: "turnStart", atMs, tavernTurn: turn });
    if (turn >= 4) {
      markers.push({
        kind: "combatStart",
        atMs: Math.min(duration - 1, Math.round(atMs + turnStep * 0.68)),
        tavernTurn: turn,
      });
    }
  }

  markers.push({
    kind: "matchEnd",
    atMs: Math.max(0, duration - 500),
    tavernTurn: match.tavernTurns,
  });

  return markers.sort((a, b) => a.atMs - b.atMs);
}

function cloneMatch(match: MatchSummary): MatchSummary {
  return { ...match };
}

function wait(ms = 140): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

class MockRpcClient implements RpcClient {
  readonly mode = "mock" as const;
  private state: CoordinatorState = "recording";
  private readonly listeners = new Map<RpcNotification, Set<(params: never) => void>>();

  async request<M extends RpcMethod>(
    method: M,
    ...args: RpcArgs<M>
  ): Promise<RpcMethodMap[M]["result"]> {
    await wait();
    const params = (args as unknown[])[0] as RpcMethodMap[M]["params"];

    switch (method) {
      case "library.list":
        return {
          coordinatorState: this.state,
          matches: mockMatches.map(cloneMatch),
        } as RpcMethodMap[M]["result"];

      case "library.get": {
        const { matchId } = params as RpcMethodMap["library.get"]["params"];
        const match = mockMatches.find((candidate) => candidate.id === matchId);
        if (!match) {
          throw new Error(`Mock match ${matchId} was not found.`);
        }
        return {
          match: cloneMatch(match),
          markers: markersFor(match),
        } as RpcMethodMap[M]["result"];
      }

      case "library.setStarred": {
        const { matchId, starred } = params as RpcMethodMap["library.setStarred"]["params"];
        const match = mockMatches.find((candidate) => candidate.id === matchId);
        if (!match) {
          throw new Error(`Mock match ${matchId} was not found.`);
        }
        match.starred = starred;
        return { starred } as RpcMethodMap[M]["result"];
      }

      case "recorder.stop":
        this.setState("finalizing");
        await wait(700);
        this.setState("armed");
        return { state: this.state } as RpcMethodMap[M]["result"];

      case "recorder.pause":
        this.setState("paused");
        return { state: this.state } as RpcMethodMap[M]["result"];

      case "recorder.resume":
        this.setState("armed");
        return { state: this.state } as RpcMethodMap[M]["result"];

      default:
        throw new Error(`Unsupported mock RPC method: ${String(method)}`);
    }
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

  private setState(state: CoordinatorState): void {
    this.state = state;
    const params: RpcNotificationMap["recorder.stateChanged"] = { state };
    for (const listener of this.listeners.get("recorder.stateChanged") ?? []) {
      listener(params as never);
    }
  }
}

export function createMockRpcClient(): RpcClient {
  return new MockRpcClient();
}
