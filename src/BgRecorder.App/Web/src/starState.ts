import type { CoordinatorState, MatchSummary } from "./types";

export interface StarMutation {
  version: number;
  starred: boolean;
  pending: boolean;
}

export interface StarReadFence {
  version: number;
  pendingMatchIds: ReadonlySet<number>;
}

/** Captures which optimistic writes an async read is allowed to supersede. */
export function createStarReadFence(
  version: number,
  mutations: ReadonlyMap<number, StarMutation>,
): StarReadFence {
  return {
    version,
    pendingMatchIds: new Set(
      [...mutations.entries()]
        .filter(([, mutation]) => mutation.pending)
        .map(([matchId]) => matchId),
    ),
  };
}

/**
 * A read that began before a mutation, or while that mutation was pending, cannot safely replace
 * the mutation's optimistic value. A read begun after the native write has replied is authoritative.
 */
export function protectStarredFromStaleRead(
  match: MatchSummary,
  fence: StarReadFence,
  mutations: ReadonlyMap<number, StarMutation>,
): MatchSummary {
  const mutation = mutations.get(match.id);
  if (!mutation || (mutation.version <= fence.version && !fence.pendingMatchIds.has(match.id))) {
    return match;
  }

  return { ...match, starred: mutation.starred };
}

/** The match row is committed before the coordinator leaves Finalizing. */
export function shouldReloadLibraryAfterStateChange(
  previousState: CoordinatorState,
  nextState: CoordinatorState,
): boolean {
  return previousState === "finalizing" && nextState !== "finalizing";
}

/** A list's coordinator snapshot is stale as soon as any newer native notification arrives. */
export function isCoordinatorSnapshotCurrent(
  requestNotificationVersion: number,
  currentNotificationVersion: number,
): boolean {
  return requestNotificationVersion === currentNotificationVersion;
}
