import type { CoordinatorState, MatchSummary } from "./types";

/**
 * Optimistic per-match field edits (star, manual rating) and the read fence that stops a slower
 * in-flight library.list / library.get from overwriting a newer optimistic value. The fence and
 * pruning are field-agnostic; only the per-field `protect*` helpers know which column they guard.
 */
export interface FieldMutation {
  version: number;
  pending: boolean;
}

export interface StarMutation extends FieldMutation {
  starred: boolean;
}

export interface RatingMutation extends FieldMutation {
  rating: number | null;
}

export interface ReadFence {
  version: number;
  pendingMatchIds: ReadonlySet<number>;
}

/** Captures which optimistic writes an async read is allowed to supersede. */
export function createReadFence(
  version: number,
  mutations: ReadonlyMap<number, FieldMutation>,
): ReadFence {
  return {
    version,
    pendingMatchIds: new Set(
      [...mutations.entries()]
        .filter(([, mutation]) => mutation.pending)
        .map(([matchId]) => matchId),
    ),
  };
}

/** True when a read is authoritative for this match's mutation (may overwrite the optimistic value). */
function readSupersedes(
  matchId: number,
  mutation: FieldMutation | undefined,
  fence: ReadFence,
): boolean {
  return !mutation || (mutation.version <= fence.version && !fence.pendingMatchIds.has(matchId));
}

/**
 * A read that began before a mutation, or while that mutation was pending, cannot safely replace
 * the mutation's optimistic value. A read begun after the native write has replied is authoritative.
 */
export function protectStarredFromStaleRead(
  match: MatchSummary,
  fence: ReadFence,
  mutations: ReadonlyMap<number, StarMutation>,
): MatchSummary {
  const mutation = mutations.get(match.id);
  if (readSupersedes(match.id, mutation, fence)) {
    return match;
  }

  return { ...match, starred: mutation!.starred };
}

/** The manual-rating counterpart of {@link protectStarredFromStaleRead}. */
export function protectManualRatingFromStaleRead(
  match: MatchSummary,
  fence: ReadFence,
  mutations: ReadonlyMap<number, RatingMutation>,
): MatchSummary {
  const mutation = mutations.get(match.id);
  if (readSupersedes(match.id, mutation, fence)) {
    return match;
  }

  return { ...match, manualRating: mutation!.rating };
}

/**
 * Drops committed mutations that no in-flight read still depends on, keeping the history bounded. A
 * read depends on a mutation exactly when it would protect that match's optimistic value: the
 * mutation is newer than the read's fence, or the read captured the match as pending. Pending
 * mutations are always kept. The map is mutated in place so the caller's ref identity is preserved.
 */
export function pruneMutations(
  mutations: Map<number, FieldMutation>,
  outstandingFences: readonly ReadFence[],
): void {
  for (const [matchId, mutation] of mutations) {
    if (mutation.pending) {
      continue;
    }
    const stillNeeded = outstandingFences.some(
      (fence) => mutation.version > fence.version || fence.pendingMatchIds.has(matchId),
    );
    if (!stillNeeded) {
      mutations.delete(matchId);
    }
  }
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
