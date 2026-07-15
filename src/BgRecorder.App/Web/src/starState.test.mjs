import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { rolldown } from "rolldown";

const bundle = await rolldown({
  input: fileURLToPath(new URL("./starState.ts", import.meta.url)),
});
const { output } = await bundle.generate({ format: "esm" });
await bundle.close();
const code = output.find((item) => item.type === "chunk")?.code;
assert.ok(code, "starState.ts did not produce an executable test chunk");
const moduleUrl = `data:text/javascript;base64,${Buffer.from(code).toString("base64")}`;
const {
  createReadFence,
  isCoordinatorSnapshotCurrent,
  protectManualRatingFromStaleRead,
  protectStarredFromStaleRead,
  pruneMutations,
  shouldReloadLibraryAfterStateChange,
} = await import(moduleUrl);

function match(starred = false) {
  return {
    id: 42,
    startedAt: "2026-07-15T00:00:00Z",
    gameType: "solo",
    heroCardId: null,
    place: 1,
    tavernTurns: 12,
    videoStatus: "complete",
    videoSizeBytes: 1,
    videoDurationMs: 1,
    starred,
    manualRating: null,
    mediaUrl: null,
  };
}

test("a mutation begun after a read protects its optimistic value", () => {
  const mutations = new Map();
  const fence = createReadFence(0, mutations);
  mutations.set(42, { version: 1, starred: true, pending: false });

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, true);
});

test("a mutation pending when a read starts stays protected after it commits", () => {
  const mutations = new Map([[42, { version: 1, starred: true, pending: true }]]);
  const fence = createReadFence(1, mutations);
  mutations.set(42, { version: 1, starred: true, pending: false });

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, true);
});

test("a read started after a committed mutation is authoritative", () => {
  const mutations = new Map([[42, { version: 1, starred: true, pending: false }]]);
  const fence = createReadFence(1, mutations);

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, false);
});

test("a failed second toggle keeps the earlier success ahead of a pre-success read", () => {
  const mutations = new Map();
  const preSuccessFence = createReadFence(0, mutations);

  mutations.set(42, { version: 1, starred: true, pending: false });
  // The second toggle tried to write false, failed, and completed by rolling back to true.
  mutations.set(42, { version: 2, starred: true, pending: false });

  assert.equal(
    protectStarredFromStaleRead(match(false), preSuccessFence, mutations).starred,
    true,
  );
});

test("a rating mutation begun after a read protects its optimistic value", () => {
  const mutations = new Map();
  const fence = createReadFence(0, mutations);
  mutations.set(42, { version: 1, rating: 4200, pending: false });

  assert.equal(protectManualRatingFromStaleRead(match(), fence, mutations).manualRating, 4200);
});

test("a rating read started after a committed mutation is authoritative", () => {
  const mutations = new Map([[42, { version: 1, rating: 4200, pending: false }]]);
  const fence = createReadFence(1, mutations);

  assert.equal(protectManualRatingFromStaleRead(match(), fence, mutations).manualRating, null);
});

test("a pending rating clear stays protected as null after a stale read lands", () => {
  const mutations = new Map([[42, { version: 1, rating: null, pending: true }]]);
  const fence = createReadFence(1, mutations);
  mutations.set(42, { version: 1, rating: null, pending: false });

  const stale = { ...match(), manualRating: 6000 };
  assert.equal(protectManualRatingFromStaleRead(stale, fence, mutations).manualRating, null);
});

test("prune drops a committed mutation when no reads are outstanding", () => {
  const mutations = new Map([[42, { version: 1, starred: true, pending: false }]]);
  pruneMutations(mutations, []);
  assert.equal(mutations.has(42), false);
});

test("prune keeps a pending mutation", () => {
  const mutations = new Map([[42, { version: 3, starred: true, pending: true }]]);
  pruneMutations(mutations, []);
  assert.equal(mutations.has(42), true);
});

test("prune keeps a mutation newer than an outstanding read's fence", () => {
  const mutations = new Map([[42, { version: 5, starred: true, pending: false }]]);
  // A read that began at fence version 4 predates this mutation and would still protect it.
  pruneMutations(mutations, [{ version: 4, pendingMatchIds: new Set() }]);
  assert.equal(mutations.has(42), true);
});

test("prune keeps a mutation an outstanding read captured as pending", () => {
  const mutations = new Map([[42, { version: 2, starred: true, pending: false }]]);
  // The read started while 42 was still pending, so it must keep protecting 42 after it commits.
  pruneMutations(mutations, [{ version: 9, pendingMatchIds: new Set([42]) }]);
  assert.equal(mutations.has(42), true);
});

test("prune drops a committed mutation once every outstanding read is authoritative for it", () => {
  const mutations = new Map([[42, { version: 2, starred: true, pending: false }]]);
  // A read begun at fence version 2 that did not capture 42 as pending already reads the commit.
  pruneMutations(mutations, [{ version: 2, pendingMatchIds: new Set() }]);
  assert.equal(mutations.has(42), false);
});

test("prune keeps a mutation if any one outstanding read still needs it", () => {
  const mutations = new Map([[42, { version: 5, starred: true, pending: false }]]);
  const authoritative = { version: 9, pendingMatchIds: new Set() };
  const stale = { version: 4, pendingMatchIds: new Set() };
  pruneMutations(mutations, [authoritative, stale]);
  assert.equal(mutations.has(42), true);
});

test("leaving finalizing requests a library reload", () => {
  assert.equal(shouldReloadLibraryAfterStateChange("recording", "finalizing"), false);
  assert.equal(shouldReloadLibraryAfterStateChange("finalizing", "armed"), true);
  assert.equal(shouldReloadLibraryAfterStateChange("finalizing", "paused"), true);
});

test("a stale list state cannot erase finalizing before armed arrives", () => {
  const listStartNotificationVersion = 7;
  let currentNotificationVersion = listStartNotificationVersion;
  let state = "recording";

  currentNotificationVersion += 1;
  state = "finalizing";
  if (isCoordinatorSnapshotCurrent(listStartNotificationVersion, currentNotificationVersion)) {
    state = "recording";
  }

  assert.equal(state, "finalizing");
  assert.equal(shouldReloadLibraryAfterStateChange(state, "armed"), true);
});
