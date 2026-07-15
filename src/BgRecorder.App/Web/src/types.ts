export type CoordinatorState =
  | "gameNotFound"
  | "armed"
  | "recording"
  | "finalizing"
  | "paused"
  | "storageBlocked";

export type GameType = "notBattlegrounds" | "solo" | "duos";
export type VideoStatus = "complete" | "incomplete" | "missing";
export type MarkerKind = "combatStart" | "turnStart" | "matchEnd";

/**
 * Native enum values are accepted as numbers as well as strings so the SPA remains compatible
 * with System.Text.Json's default numeric enum encoding. The bridge's preferred wire format is the
 * camel-case string form above.
 */
export type CoordinatorStateValue = CoordinatorState | number;
export type GameTypeValue = GameType | number;
export type VideoStatusValue = VideoStatus | number;
export type MarkerKindValue = MarkerKind | number;

export interface MatchSummary {
  id: number;
  startedAt: string;
  gameType: GameTypeValue;
  heroCardId: string | null;
  place: number | null;
  tavernTurns: number;
  videoStatus: VideoStatusValue;
  videoSizeBytes: number | null;
  videoDurationMs: number | null;
  starred: boolean;
  manualRating: number | null;
  mediaUrl: string | null;
}

export interface Marker {
  kind: MarkerKindValue;
  atMs: number;
  tavernTurn: number;
}

export interface LibraryListResult {
  coordinatorState: CoordinatorStateValue;
  matches: MatchSummary[];
}

export interface MatchDetailResult {
  match: MatchSummary;
  markers: Marker[];
}

export interface RecorderCommandResult {
  state: CoordinatorStateValue;
}

export interface RpcMethodMap {
  "library.list": {
    params: undefined;
    result: LibraryListResult;
  };
  "library.get": {
    params: { matchId: number };
    result: MatchDetailResult;
  };
  "library.setStarred": {
    params: { matchId: number; starred: boolean };
    result: null | { starred: boolean };
  };
  "recorder.stop": {
    params: undefined;
    result: RecorderCommandResult;
  };
  "recorder.pause": {
    params: undefined;
    result: RecorderCommandResult;
  };
  "recorder.resume": {
    params: undefined;
    result: RecorderCommandResult;
  };
}

export interface RpcNotificationMap {
  "recorder.stateChanged": {
    state: CoordinatorStateValue;
  };
}

export type RpcMethod = keyof RpcMethodMap;
export type RpcNotification = keyof RpcNotificationMap;
export type RpcArgs<M extends RpcMethod> = RpcMethodMap[M]["params"] extends undefined
  ? []
  : [params: RpcMethodMap[M]["params"]];

export interface RpcClient {
  readonly mode: "native" | "mock";
  request<M extends RpcMethod>(
    method: M,
    ...args: RpcArgs<M>
  ): Promise<RpcMethodMap[M]["result"]>;
  on<N extends RpcNotification>(
    method: N,
    handler: (params: RpcNotificationMap[N]) => void,
  ): () => void;
}

function compactEnum(value: unknown): string {
  return String(value).replace(/[\s_-]/g, "").toLowerCase();
}

export function normalizeCoordinatorState(value: CoordinatorStateValue | string): CoordinatorState {
  if (typeof value === "number") {
    return (["gameNotFound", "armed", "recording", "finalizing", "paused", "storageBlocked"] as const)[value]
      ?? "gameNotFound";
  }

  switch (compactEnum(value)) {
    case "armed":
    case "idle":
      return "armed";
    case "recording":
      return "recording";
    case "finalizing":
      return "finalizing";
    case "paused":
      return "paused";
    case "storageblocked":
      return "storageBlocked";
    case "gamenotfound":
    case "disconnected":
    default:
      return "gameNotFound";
  }
}

export function normalizeGameType(value: GameTypeValue | string): GameType {
  if (typeof value === "number") {
    return (["notBattlegrounds", "solo", "duos"] as const)[value] ?? "notBattlegrounds";
  }

  switch (compactEnum(value)) {
    case "solo":
      return "solo";
    case "duos":
    case "duo":
      return "duos";
    default:
      return "notBattlegrounds";
  }
}

export function normalizeVideoStatus(value: VideoStatusValue | string): VideoStatus {
  if (typeof value === "number") {
    return (["complete", "incomplete", "missing"] as const)[value] ?? "missing";
  }

  switch (compactEnum(value)) {
    case "complete":
      return "complete";
    case "incomplete":
      return "incomplete";
    default:
      return "missing";
  }
}

export function normalizeMarkerKind(value: MarkerKindValue | string): MarkerKind {
  if (typeof value === "number") {
    return (["combatStart", "turnStart", "matchEnd"] as const)[value] ?? "turnStart";
  }

  switch (compactEnum(value)) {
    case "combatstart":
      return "combatStart";
    case "matchend":
      return "matchEnd";
    default:
      return "turnStart";
  }
}
