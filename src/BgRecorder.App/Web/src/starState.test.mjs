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
  createStarReadFence,
  isCoordinatorSnapshotCurrent,
  protectStarredFromStaleRead,
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
  const fence = createStarReadFence(0, mutations);
  mutations.set(42, { version: 1, starred: true, pending: false });

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, true);
});

test("a mutation pending when a read starts stays protected after it commits", () => {
  const mutations = new Map([[42, { version: 1, starred: true, pending: true }]]);
  const fence = createStarReadFence(1, mutations);
  mutations.set(42, { version: 1, starred: true, pending: false });

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, true);
});

test("a read started after a committed mutation is authoritative", () => {
  const mutations = new Map([[42, { version: 1, starred: true, pending: false }]]);
  const fence = createStarReadFence(1, mutations);

  assert.equal(protectStarredFromStaleRead(match(false), fence, mutations).starred, false);
});

test("a failed second toggle keeps the earlier success ahead of a pre-success read", () => {
  const mutations = new Map();
  const preSuccessFence = createStarReadFence(0, mutations);

  mutations.set(42, { version: 1, starred: true, pending: false });
  // The second toggle tried to write false, failed, and completed by rolling back to true.
  mutations.set(42, { version: 2, starred: true, pending: false });

  assert.equal(
    protectStarredFromStaleRead(match(false), preSuccessFence, mutations).starred,
    true,
  );
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
