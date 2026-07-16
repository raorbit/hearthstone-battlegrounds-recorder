import { type JSX } from "preact";
import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { bridge } from "./bridge";
import {
  createReadFence,
  isCoordinatorSnapshotCurrent,
  protectManualRatingFromStaleRead,
  protectStarredFromStaleRead,
  pruneMutations,
  shouldReloadLibraryAfterStateChange,
  type RatingMutation,
  type ReadFence,
  type StarMutation,
} from "./starState";
import {
  type CoordinatorState,
  type GameType,
  type Marker,
  type MatchDetailResult,
  type MatchSummary,
  type RatingHealth,
  type SettingsResult,
  type SettingsUpdate,
  type StoragePreview,
  type StorageSettings,
  type StorageUpdate,
  type StorageVolume,
  normalizeCoordinatorState,
  normalizeGameType,
  normalizeMarkerKind,
  normalizeVideoStatus,
} from "./types";

/** The per-field read fences a single async library read is guarded by. */
interface ReadFences {
  star: ReadFence;
  rating: ReadFence;
}

type Bucket = "all" | "solo" | "duos" | "starred";
type Segment = "all" | "top" | "bottom";
type DateFilter = "all" | "today" | "7d" | "30d" | "90d";
type RecorderCommand = "recorder.stop" | "recorder.pause" | "recorder.resume";
type Notice = { tone: "success" | "error"; text: string };
type View = "library" | "settings" | "storage";

const FPS_MIN = 15;
const FPS_MAX = 240;
const BITRATE_MIN = 1;
const BITRATE_MAX = 100;

const dateOptions: ReadonlyArray<{ value: DateFilter; label: string }> = [
  { value: "all", label: "All dates" },
  { value: "today", label: "Today" },
  { value: "7d", label: "Last 7 days" },
  { value: "30d", label: "Last 30 days" },
  { value: "90d", label: "Last 90 days" },
];

const statusMetadata: Record<CoordinatorState, {
  eyebrow: string;
  title: string;
  detail: string;
  tone: string;
}> = {
  gameNotFound: {
    eyebrow: "GAME NOT FOUND",
    title: "Hearthstone not detected",
    detail: "The recorder will reconnect automatically.",
    tone: "disconnected",
  },
  armed: {
    eyebrow: "LOG FEED CONNECTED",
    title: "Waiting for a match",
    detail: "Auto-recording is armed.",
    tone: "armed",
  },
  recording: {
    eyebrow: "RECORDING LIVE",
    title: "Recording this match",
    detail: "Video and game events are being captured.",
    tone: "recording",
  },
  finalizing: {
    eyebrow: "SAVING MATCH",
    title: "Finalizing recording",
    detail: "Muxing audio and writing the library row.",
    tone: "finalizing",
  },
  paused: {
    eyebrow: "AUTO-RECORDING OFF",
    title: "Recording paused",
    detail: "Resume when you want to capture matches again.",
    tone: "paused",
  },
  storageBlocked: {
    eyebrow: "STORAGE SAFETY BLOCK",
    title: "Not enough free space",
    detail: "Free space is required before the next match can be armed.",
    tone: "storage",
  },
};

function asErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function formatHero(cardId: string | null): string {
  return cardId?.trim() || "Unknown hero";
}

function heroInitials(cardId: string | null): string {
  if (!cardId) {
    return "?";
  }

  const parts = cardId.split(/[\s_-]+/).filter(Boolean);
  const useful = parts.filter((part) => !["TB", "BG", "BACONSHOP", "HERO"].includes(part.toUpperCase()));
  const source = useful.length > 0 ? useful : parts;
  return source.slice(-2).map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "?";
}

function ordinal(place: number | null): string {
  if (place === null) {
    return "—";
  }

  const mod100 = place % 100;
  if (mod100 >= 11 && mod100 <= 13) {
    return `${place}th`;
  }

  const suffix = place % 10 === 1 ? "st" : place % 10 === 2 ? "nd" : place % 10 === 3 ? "rd" : "th";
  return `${place}${suffix}`;
}

function formatDuration(milliseconds: number | null): string {
  if (milliseconds === null || !Number.isFinite(milliseconds) || milliseconds < 0) {
    return "—";
  }

  const seconds = Math.round(milliseconds / 1_000);
  const hours = Math.floor(seconds / 3_600);
  const minutes = Math.floor((seconds % 3_600) / 60);
  const remainder = seconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function formatBytes(bytes: number | null): string {
  if (bytes === null || !Number.isFinite(bytes) || bytes < 0) {
    return "—";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1_024 && unit < units.length - 1) {
    value /= 1_024;
    unit += 1;
  }
  return `${value.toFixed(unit >= 3 ? 1 : 0)} ${units[unit]}`;
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "Unknown";
  }

  const now = new Date();
  const sameDay = date.toDateString() === now.toDateString();
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  const time = date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

  if (sameDay) {
    return `Today, ${time}`;
  }
  if (date.toDateString() === yesterday.toDateString()) {
    return `Yesterday, ${time}`;
  }
  return date.toLocaleDateString([], { month: "short", day: "numeric", year: date.getFullYear() === now.getFullYear() ? undefined : "numeric" });
}

function topThreshold(match: MatchSummary): number {
  return normalizeGameType(match.gameType) === "duos" ? 2 : 4;
}

function gameTypeLabel(value: MatchSummary["gameType"]): string {
  switch (normalizeGameType(value)) {
    case "solo":
      return "Solo";
    case "duos":
      return "Duos";
    default:
      return "Recovered";
  }
}

function isInDateRange(match: MatchSummary, filter: DateFilter): boolean {
  if (filter === "all") {
    return true;
  }

  const started = new Date(match.startedAt);
  if (Number.isNaN(started.getTime())) {
    return false;
  }

  const now = new Date();
  if (filter === "today") {
    return started.toDateString() === now.toDateString();
  }

  const days = filter === "7d" ? 7 : filter === "30d" ? 30 : 90;
  return started.getTime() >= now.getTime() - days * 24 * 60 * 60 * 1_000;
}

function markerLabel(marker: Marker): string {
  switch (normalizeMarkerKind(marker.kind)) {
    case "combatStart":
      return `Turn ${marker.tavernTurn} combat`;
    case "matchEnd":
      return "Match end";
    case "turnStart":
      return `Turn ${marker.tavernTurn}`;
  }
}

interface StatusCardProps {
  state: CoordinatorState;
  pendingCommand: RecorderCommand | null;
  onCommand(command: RecorderCommand): void;
}

function StatusCard({ state, pendingCommand, onCommand }: StatusCardProps): JSX.Element {
  const metadata = statusMetadata[state];
  const busy = pendingCommand !== null;

  return (
    <section class={`status-card status-card--${metadata.tone}`} aria-live="polite">
      <div class="status-card__eyebrow">
        <span class="status-dot" aria-hidden="true" />
        {metadata.eyebrow}
      </div>
      <strong class="status-card__title">{metadata.title}</strong>
      <p class="status-card__detail">{metadata.detail}</p>
      <div class="status-card__actions">
        {state === "recording" && (
          <button
            class="button button--danger"
            type="button"
            disabled={busy}
            onClick={() => onCommand("recorder.stop")}
          >
            {pendingCommand === "recorder.stop" ? "Stopping…" : "Stop this recording"}
          </button>
        )}
        {state === "paused" ? (
          <button
            class="button button--primary"
            type="button"
            disabled={busy}
            onClick={() => onCommand("recorder.resume")}
          >
            {pendingCommand === "recorder.resume" ? "Resuming…" : "Resume auto-recording"}
          </button>
        ) : (
          <button
            class="button button--quiet"
            type="button"
            disabled={busy || state === "finalizing"}
            onClick={() => onCommand("recorder.pause")}
          >
            {pendingCommand === "recorder.pause" ? "Pausing…" : "Pause auto-recording"}
          </button>
        )}
      </div>
    </section>
  );
}

interface BucketButtonProps {
  active: boolean;
  count: number;
  label: string;
  onClick(): void;
}

function BucketButton({ active, count, label, onClick }: BucketButtonProps): JSX.Element {
  return (
    <button
      class={`bucket-button${active ? " bucket-button--active" : ""}`}
      type="button"
      aria-pressed={active}
      onClick={onClick}
    >
      <span class="bucket-button__diamond" aria-hidden="true" />
      <span>{label}</span>
      <span class="bucket-button__count">{count}</span>
    </button>
  );
}

interface RatingEditorProps {
  match: MatchSummary;
  pending: boolean;
  onSet(rating: number | null): void;
}

/** Inline per-match manual rating entry — v1's only rating source. Commits on blur/Enter. */
function RatingEditor({ match, pending, onSet }: RatingEditorProps): JSX.Element {
  const [draft, setDraft] = useState(match.manualRating?.toString() ?? "");
  // Escape blurs the input to defocus it, which would otherwise fire onBlur → commit against the
  // still-typed draft. This ref makes that one commit a no-op so Escape truly discards the edit.
  const cancellingRef = useRef(false);

  useEffect(() => {
    setDraft(match.manualRating?.toString() ?? "");
  }, [match.id, match.manualRating]);

  const revert = (): void => setDraft(match.manualRating?.toString() ?? "");

  const commit = (): void => {
    if (cancellingRef.current) {
      cancellingRef.current = false;
      return; // this blur came from an Escape cancellation; discard the typed value
    }
    const trimmed = draft.trim();
    if (trimmed === "") {
      onSet(null);
      return;
    }
    const value = Number(trimmed);
    if (!Number.isInteger(value) || value < 0 || value > 100_000) {
      revert();
      return;
    }
    onSet(value);
  };

  return (
    <label class="rating-editor">
      <span class="rating-editor__label">Rating</span>
      <input
        class="rating-editor__input mono"
        type="number"
        inputMode="numeric"
        min={0}
        max={100_000}
        step={1}
        value={draft}
        placeholder="—"
        disabled={pending}
        aria-label="Manual rating for this match"
        onInput={(event) => setDraft(event.currentTarget.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            event.currentTarget.blur();
          } else if (event.key === "Escape") {
            cancellingRef.current = true;
            revert();
            event.currentTarget.blur();
          }
        }}
      />
      {pending && <span class="spinner spinner--small" aria-hidden="true" />}
    </label>
  );
}

interface RatingCardProps {
  matches: MatchSummary[];
  bucket: Bucket;
  health: RatingHealth | null;
}

/**
 * Per-mode manual-rating summary. Follows the active solo/duos bucket (the plan's "strictly
 * per-mode" card); with no manual entries it shows the degraded "—". When the rating provider is not
 * OK (v1 always ships it disabled) a non-blocking "automatic MMR unavailable" note is shown.
 */
function RatingCard({ matches, bucket, health }: RatingCardProps): JSX.Element {
  const mode: GameType = bucket === "duos" ? "duos" : "solo";
  const modeLabel = mode === "duos" ? "Duos" : "Solo";
  const rated = matches
    .filter((candidate) => normalizeGameType(candidate.gameType) === mode && candidate.manualRating !== null)
    .slice()
    .sort((a, b) => new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime());
  const healthNote = health !== null && health !== "ok"
    ? <p class="rating-card__health">Automatic MMR unavailable — recordings unaffected.</p>
    : null;

  if (rated.length === 0) {
    return (
      <section class="rating-card" aria-label={`${modeLabel} rating`}>
        <div class="rating-card__head">
          <span class="rating-card__mode">{modeLabel} rating</span>
          <span class="rating-card__value rating-card__value--empty">—</span>
        </div>
        <p class="rating-card__note">No manual ratings yet — add one on any {modeLabel.toLowerCase()} match to track it here.</p>
        {healthNote}
      </section>
    );
  }

  const values = rated.slice(-12).map((candidate) => candidate.manualRating as number);
  const latest = values[values.length - 1];
  const delta = values.length > 1 ? latest - values[values.length - 2] : null;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const points = values
    .map((value, index) => {
      const x = values.length === 1 ? 100 : (index / (values.length - 1)) * 100;
      const y = 100 - ((value - min) / range) * 100;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");

  return (
    <section class="rating-card" aria-label={`${modeLabel} rating`}>
      <div class="rating-card__head">
        <span class="rating-card__mode">{modeLabel} rating</span>
        <span class="rating-card__value mono">{latest.toLocaleString()}</span>
        {delta !== null && delta !== 0 && (
          <span class={`rating-card__delta rating-card__delta--${delta > 0 ? "up" : "down"}`}>
            {delta > 0 ? "▲" : "▼"} {Math.abs(delta).toLocaleString()}
          </span>
        )}
      </div>
      {values.length > 1 && (
        <svg class="rating-card__spark" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
          <polyline points={points} />
        </svg>
      )}
      <p class="rating-card__note">Manual entries · {rated.length} rated {modeLabel.toLowerCase()} {rated.length === 1 ? "match" : "matches"}</p>
      {healthNote}
    </section>
  );
}

interface PlayerProps {
  match: MatchSummary | null;
  detail: MatchDetailResult | null;
  loading: boolean;
  error: string | null;
  ratingPending: boolean;
  videoRef: { current: HTMLVideoElement | null };
  onRetry(): void;
  onSetRating(match: MatchSummary, rating: number | null): void;
}

/** A play-only segment of the VOD (no file is cut) — a single fight, combat-start to the next marker. */
interface Clip {
  turn: number;
  startMs: number;
  endMs: number;
}

function Player({ match, detail, loading, error, ratingPending, videoRef, onRetry, onSetRating }: PlayerProps): JSX.Element {
  const [currentTime, setCurrentTime] = useState(0);
  const [mediaDuration, setMediaDuration] = useState(0);
  const [mediaError, setMediaError] = useState<string | null>(null);
  // When set, playback auto-pauses once currentTime reaches this ms — the end of a virtual clip.
  const [clipEnd, setClipEnd] = useState<number | null>(null);
  const activeMatch = detail?.match ?? match;

  useEffect(() => {
    setCurrentTime(0);
    setMediaDuration(0);
    setMediaError(null);
    setClipEnd(null);
  }, [activeMatch?.id, activeMatch?.mediaUrl]);

  // Playback keyboard shortcuts, owned here so every seek cancels an armed clip (same as a manual
  // seek). Deferred to focused controls/rows and the video's own native controls. The Player only
  // mounts in the library view, so no view guard is needed.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const active = document.activeElement;
      const tag = active?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || (active as HTMLElement | null)?.isContentEditable) {
        return;
      }
      if (tag === "BUTTON" || tag === "TR" || tag === "A" || active === videoRef.current) {
        return;
      }
      const video = videoRef.current;
      if (!video) {
        return;
      }

      // A keyboard seek cancels any armed clip and updates both the element and the timeline state.
      const seek = (milliseconds: number): void => {
        setClipEnd(null);
        const max = Number.isFinite(video.duration) ? video.duration : Number.POSITIVE_INFINITY;
        const seconds = Math.min(max, Math.max(0, milliseconds / 1_000));
        setCurrentTime(seconds);
        try {
          video.currentTime = seconds;
        } catch {
          // no media source in preview
        }
      };

      if (event.key === " ") {
        event.preventDefault();
        if (video.paused) {
          void video.play().catch(() => undefined);
        } else {
          video.pause();
        }
      } else if (event.key === "ArrowLeft") {
        event.preventDefault();
        seek((video.currentTime - 5) * 1_000);
      } else if (event.key === "ArrowRight") {
        event.preventDefault();
        seek((video.currentTime + 5) * 1_000);
      } else if (event.key === "[" || event.key === "]") {
        const marks = (detail?.markers ?? []).map((marker) => marker.atMs).sort((a, b) => a - b);
        if (marks.length === 0) {
          return;
        }
        event.preventDefault();
        const nowMs = video.currentTime * 1_000;
        const next = event.key === "]"
          ? marks.find((atMs) => atMs > nowMs + 250)
          : [...marks].reverse().find((atMs) => atMs < nowMs - 250);
        if (next !== undefined) {
          seek(next);
        }
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [detail, videoRef]);

  const durationMs = mediaDuration > 0
    ? mediaDuration * 1_000
    : activeMatch?.videoDurationMs ?? 0;
  const progress = durationMs > 0 ? Math.min(1, Math.max(0, currentTime * 1_000 / durationMs)) : 0;
  const mediaAvailable = Boolean(activeMatch?.mediaUrl) && normalizeVideoStatus(activeMatch?.videoStatus ?? "missing") !== "missing";

  const seekTo = (milliseconds: number): void => {
    if (durationMs <= 0) {
      return;
    }
    const targetSeconds = Math.min(durationMs, Math.max(0, milliseconds)) / 1_000;
    setCurrentTime(targetSeconds);
    if (videoRef.current && mediaAvailable) {
      try {
        videoRef.current.currentTime = targetSeconds;
      } catch {
        // The mock has no media source; keeping the timeline state updated is still useful in preview.
      }
    }
  };

  // A manual seek cancels any active clip so playback no longer auto-pauses at the old clip end.
  const seekManually = (milliseconds: number): void => {
    setClipEnd(null);
    seekTo(milliseconds);
  };

  // Virtual clip: seek to the fight's start, arm the auto-pause at its end, and start playing. No file
  // is extracted — it's just a bounded playback of the existing VOD.
  const playClip = (clip: Clip): void => {
    setClipEnd(clip.endMs);
    seekTo(clip.startMs);
    const video = videoRef.current;
    if (video && mediaAvailable) {
      void video.play().catch(() => {
        // Autoplay/seek can reject (no media in preview, or a transient state); the timeline still moved.
      });
    }
  };

  const markers = detail?.markers ?? [];
  const clips: Clip[] = markers
    .map((marker, index) => ({ marker, next: markers[index + 1] }))
    .filter(({ marker }) => normalizeMarkerKind(marker.kind) === "combatStart")
    .map(({ marker, next }) => ({
      turn: marker.tavernTurn,
      startMs: marker.atMs,
      endMs: next ? next.atMs : durationMs,
    }));

  const handleTimelineClick = (event: JSX.TargetedMouseEvent<HTMLDivElement>): void => {
    if (durationMs <= 0) {
      return;
    }
    const bounds = event.currentTarget.getBoundingClientRect();
    const ratio = Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width));
    seekManually(durationMs * ratio);
  };

  if (!match) {
    return (
      <section class="player-panel player-panel--empty">
        <div class="empty-state__glyph" aria-hidden="true">◇</div>
        <strong>Select a recording</strong>
        <span>Choose a match below to load its video and timeline.</span>
      </section>
    );
  }

  return (
    <section class="player-panel" aria-label="Selected recording">
      <div class="player-stage">
        <video
          key={activeMatch?.id}
          ref={videoRef}
          class="player-video"
          src={activeMatch?.mediaUrl ?? undefined}
          poster={activeMatch?.thumbnailUrl ?? undefined}
          controls
          preload="metadata"
          onLoadedMetadata={(event) => {
            setMediaDuration(Number.isFinite(event.currentTarget.duration) ? event.currentTarget.duration : 0);
            setMediaError(null);
          }}
          onTimeUpdate={(event) => {
            const seconds = event.currentTarget.currentTime;
            setCurrentTime(seconds);
            if (clipEnd !== null && seconds * 1_000 >= clipEnd) {
              event.currentTarget.pause();
              setClipEnd(null);
            }
          }}
          onError={() => setMediaError("The recording could not be opened.")}
        >
          Your browser does not support HTML5 video playback.
        </video>

        <div class="player-stage__wash" aria-hidden="true" />
        <div class="player-stage__header">
          <div>
            <span class="placement-pill">{ordinal(activeMatch?.place ?? null)}</span>
            <span class="mode-pill">{gameTypeLabel(activeMatch?.gameType ?? "notBattlegrounds")}</span>
          </div>
          <span class="turn-pill">Turn {activeMatch?.tavernTurns ?? 0}</span>
        </div>

        {!loading && !error && !mediaAvailable && (
          <div class="player-message">
            <span class="player-message__icon" aria-hidden="true">▶</span>
            <strong>{normalizeVideoStatus(activeMatch?.videoStatus ?? "missing") === "missing" ? "Recording unavailable" : "No media URL"}</strong>
            <span>{bridge.mode === "mock" ? "Browser preview uses metadata-only mock recordings." : "The recording cannot be played from its current location."}</span>
          </div>
        )}

        {loading && (
          <div class="player-message">
            <span class="spinner" aria-hidden="true" />
            <strong>Loading recording details</strong>
          </div>
        )}

        {error && (
          <div class="player-message player-message--error" role="alert">
            <strong>Could not load recording details</strong>
            <span>{error}</span>
            <button class="button button--quiet" type="button" onClick={onRetry}>Try again</button>
          </div>
        )}

        {mediaError && mediaAvailable && (
          <div class="player-message player-message--error" role="alert">
            <strong>Playback failed</strong>
            <span>{mediaError}</span>
          </div>
        )}
      </div>

      <div class="player-details">
        <div class="player-details__identity">
          <span class="hero-chip hero-chip--large">{heroInitials(activeMatch?.heroCardId ?? null)}</span>
          <div>
            <strong title={formatHero(activeMatch?.heroCardId ?? null)}>{formatHero(activeMatch?.heroCardId ?? null)}</strong>
            <span>{formatDate(activeMatch?.startedAt ?? "")} · {formatDuration(activeMatch?.videoDurationMs ?? null)}</span>
          </div>
        </div>
        {activeMatch && (
          <RatingEditor
            match={activeMatch}
            pending={ratingPending}
            onSet={(rating) => onSetRating(activeMatch, rating)}
          />
        )}
        <div class="player-details__clock" aria-label="Playback time">
          {formatDuration(Math.round(currentTime * 1_000))} / {formatDuration(durationMs || activeMatch?.videoDurationMs || null)}
        </div>
      </div>

      <div class="timeline" aria-label="Recording timeline">
        <div class="timeline-track" onClick={handleTimelineClick}>
          <div class="timeline-progress" style={{ width: `${(progress * 100).toFixed(3)}%` }} />
          {(detail?.markers ?? []).map((marker, index) => {
            const kind = normalizeMarkerKind(marker.kind);
            const left = durationMs > 0 ? Math.min(100, Math.max(0, marker.atMs / durationMs * 100)) : 0;
            return (
              <button
                key={`${kind}-${marker.atMs}-${index}`}
                class={`timeline-marker timeline-marker--${kind}`}
                type="button"
                style={{ left: `${left.toFixed(3)}%` }}
                title={`${markerLabel(marker)} · ${formatDuration(marker.atMs)}`}
                aria-label={`Seek to ${markerLabel(marker)} at ${formatDuration(marker.atMs)}`}
                onClick={(event) => {
                  event.stopPropagation();
                  seekManually(marker.atMs);
                }}
              />
            );
          })}
          <span class="timeline-head" style={{ left: `${(progress * 100).toFixed(3)}%` }} aria-hidden="true" />
        </div>
        <div class="timeline-legend" aria-hidden="true">
          <span><i class="legend-swatch legend-swatch--combat" />Combat</span>
          <span><i class="legend-swatch legend-swatch--turn" />Turn</span>
          <span><i class="legend-swatch legend-swatch--end" />Match end</span>
        </div>
      </div>

      {clips.length > 0 && (
        <div class="clips" aria-label="Combat clips">
          <span class="clips__label">Fights</span>
          <div class="clips__strip">
            {clips.map((clip, index) => (
              <button
                key={`clip-${clip.turn}-${clip.startMs}-${index}`}
                class="clip-chip"
                type="button"
                title={`Play turn ${clip.turn} fight (${formatDuration(clip.startMs)}–${formatDuration(clip.endMs)})`}
                aria-label={`Play turn ${clip.turn} fight`}
                onClick={() => playClip(clip)}
              >
                <span class="clip-chip__play" aria-hidden="true">▶</span>
                Turn {clip.turn}
              </button>
            ))}
          </div>
        </div>
      )}
    </section>
  );
}

interface MatchTableProps {
  matches: MatchSummary[];
  selectedId: number | null;
  loading: boolean;
  starPending: ReadonlySet<number>;
  deletePending: ReadonlySet<number>;
  onSelect(matchId: number): void;
  onToggleStar(match: MatchSummary): void;
  onDelete(match: MatchSummary): void;
}

function MatchTable(
  { matches, selectedId, loading, starPending, deletePending, onSelect, onToggleStar, onDelete }: MatchTableProps,
): JSX.Element {
  // Which row is asking to confirm a delete. Deletion is irreversible, so it takes an explicit
  // second click on an inline ✓ rather than firing on the first click or relying on a native dialog.
  const [confirmingId, setConfirmingId] = useState<number | null>(null);

  if (loading) {
    return (
      <div class="table-skeleton" aria-label="Loading recordings">
        {Array.from({ length: 6 }, (_, index) => <div class="skeleton-row" key={index} />)}
      </div>
    );
  }

  return (
    <div class="match-table-scroll">
      <table class="match-table">
        <thead>
          <tr>
            <th>Hero</th>
            <th>Place</th>
            <th>Mode</th>
            <th>Rating</th>
            <th>Turns</th>
            <th>Length</th>
            <th>Date</th>
            <th>Size</th>
            <th>Status</th>
            <th><span class="visually-hidden">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          {matches.map((match) => {
            const status = normalizeVideoStatus(match.videoStatus);
            const selected = selectedId === match.id;
            return (
              <tr
                key={match.id}
                class={selected ? "match-row match-row--selected" : "match-row"}
                tabIndex={0}
                aria-selected={selected}
                onClick={() => onSelect(match.id)}
                onKeyDown={(event) => {
                  if (event.target !== event.currentTarget) {
                    return;
                  }
                  if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    onSelect(match.id);
                  }
                }}
              >
                <td>
                  <div class="hero-cell">
                    <span class="hero-chip">{heroInitials(match.heroCardId)}</span>
                    <span class="hero-card-id" title={formatHero(match.heroCardId)}>{formatHero(match.heroCardId)}</span>
                  </div>
                </td>
                <td><strong class={`placement placement--${match.place !== null && match.place <= topThreshold(match) ? "top" : "bottom"}`}>{ordinal(match.place)}</strong></td>
                <td><span class="mode-label">{gameTypeLabel(match.gameType)}</span></td>
                <td class="mono">{match.manualRating?.toLocaleString() ?? "—"}</td>
                <td class="mono">{match.tavernTurns}</td>
                <td class="mono">{formatDuration(match.videoDurationMs)}</td>
                <td>{formatDate(match.startedAt)}</td>
                <td class="mono muted">{formatBytes(match.videoSizeBytes)}</td>
                <td>
                  {match.isOffline ? (
                    <span class="video-status video-status--offline" title="This recording's drive is unplugged; reconnect it to play.">offline</span>
                  ) : (
                    <span class={`video-status video-status--${status}`}>{status}</span>
                  )}
                </td>
                <td>
                  <div class="row-actions">
                    {confirmingId === match.id ? (
                      <>
                        <button
                          class="confirm-button confirm-button--yes"
                          type="button"
                          title="Confirm delete"
                          aria-label="Confirm delete"
                          disabled={deletePending.has(match.id)}
                          onClick={(event) => {
                            event.stopPropagation();
                            setConfirmingId(null);
                            onDelete(match);
                          }}
                        >
                          {deletePending.has(match.id) ? <span class="spinner spinner--small" /> : "✓"}
                        </button>
                        <button
                          class="confirm-button confirm-button--no"
                          type="button"
                          title="Cancel"
                          aria-label="Cancel delete"
                          onClick={(event) => {
                            event.stopPropagation();
                            setConfirmingId(null);
                          }}
                        >
                          ✕
                        </button>
                      </>
                    ) : (
                      <>
                        <button
                          class={`star-button${match.starred ? " star-button--active" : ""}`}
                          type="button"
                          title={match.starred ? "Remove from starred" : "Keep this recording"}
                          aria-label={match.starred ? "Remove from starred" : "Keep this recording"}
                          aria-pressed={match.starred}
                          disabled={starPending.has(match.id) || deletePending.has(match.id)}
                          onKeyDown={(event) => {
                            if (event.key === "Enter" || event.key === " ") {
                              event.preventDefault();
                              event.stopPropagation();
                              if (!event.repeat) {
                                onToggleStar(match);
                              }
                            }
                          }}
                          onClick={(event) => {
                            event.stopPropagation();
                            onToggleStar(match);
                          }}
                        >
                          {starPending.has(match.id) ? <span class="spinner spinner--small" /> : "★"}
                        </button>
                        <button
                          class="delete-button"
                          type="button"
                          title="Delete recording"
                          aria-label="Delete recording"
                          disabled={deletePending.has(match.id)}
                          onClick={(event) => {
                            event.stopPropagation();
                            setConfirmingId(match.id);
                          }}
                        >
                          {deletePending.has(match.id) ? <span class="spinner spinner--small" /> : "🗑"}
                        </button>
                      </>
                    )}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

interface SettingsViewProps {
  notify(notice: Notice): void;
}

function formToUpdate(settings: SettingsResult): SettingsUpdate {
  return {
    fps: settings.fps,
    bitrateMbps: settings.bitrateMbps,
    gameOnlyAudio: settings.gameOnlyAudio,
    mixMicrophone: settings.mixMicrophone,
  };
}

/**
 * The Settings surface (M6). Recording fields are editable and validated client-side to match the
 * native RPC's bounds; paths are read-only for now (changing the library location needs migration).
 * Saved recording changes take effect on the next launch because the running coordinator captured the
 * settings at startup — the note and toast state this rather than implying a live effect.
 */
function SettingsView({ notify }: SettingsViewProps): JSX.Element {
  const [settings, setSettings] = useState<SettingsResult | null>(null);
  const [form, setForm] = useState<SettingsUpdate | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const result = await bridge.request("settings.get");
      setSettings(result);
      setForm(formToUpdate(result));
    } catch (err) {
      setError(asErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const fpsValid = form !== null && Number.isInteger(form.fps) && form.fps >= FPS_MIN && form.fps <= FPS_MAX;
  const bitrateValid = form !== null
    && Number.isInteger(form.bitrateMbps)
    && form.bitrateMbps >= BITRATE_MIN
    && form.bitrateMbps <= BITRATE_MAX;
  const dirty = settings !== null && form !== null && (
    form.fps !== settings.fps ||
    form.bitrateMbps !== settings.bitrateMbps ||
    form.gameOnlyAudio !== settings.gameOnlyAudio ||
    form.mixMicrophone !== settings.mixMicrophone
  );
  const canSave = dirty && fpsValid && bitrateValid && !saving;

  const save = async (): Promise<void> => {
    if (form === null || !canSave) {
      return;
    }
    setSaving(true);
    try {
      const result = await bridge.request("settings.set", form);
      setSettings(result);
      setForm(formToUpdate(result));
      notify({ tone: "success", text: "Settings saved — recording changes apply after restart." });
    } catch (err) {
      notify({ tone: "error", text: `Could not save settings: ${asErrorMessage(err)}` });
    } finally {
      setSaving(false);
    }
  };

  const patch = (change: Partial<SettingsUpdate>): void =>
    setForm((current) => (current === null ? current : { ...current, ...change }));

  if (loading) {
    return (
      <main class="settings-view">
        <div class="settings-scroll">
          <div class="player-message" style={{ position: "static", margin: "40px auto" }}>
            <span class="spinner" aria-hidden="true" />
            <strong>Loading settings</strong>
          </div>
        </div>
      </main>
    );
  }

  if (error || form === null || settings === null) {
    return (
      <main class="settings-view">
        <div class="settings-scroll">
          <div class="empty-state">
            <div class="empty-state__glyph" aria-hidden="true">◇</div>
            <strong>Could not load settings</strong>
            {error && <span>{error}</span>}
            <button class="button button--quiet" type="button" onClick={() => void load()}>Try again</button>
          </div>
        </div>
      </main>
    );
  }

  return (
    <main class="settings-view">
      <div class="settings-scroll">
        <header class="settings-head">
          <h1>Settings</h1>
          <p>Recording preferences and library locations for this PC.</p>
        </header>

        <section class="settings-section" aria-label="Recording">
          <h2 class="settings-section__title">Recording</h2>
          <p class="settings-note">Changes apply to matches recorded after the next restart of BG Recorder.</p>

          <label class="settings-field">
            <span class="settings-field__label">Frames per second</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="numeric"
              min={FPS_MIN}
              max={FPS_MAX}
              step={1}
              value={form.fps}
              aria-invalid={!fpsValid}
              onInput={(event) => patch({ fps: Math.trunc(Number(event.currentTarget.value)) })}
            />
            <span class={`settings-field__hint${fpsValid ? "" : " settings-field__hint--error"}`}>
              {fpsValid ? `${FPS_MIN}–${FPS_MAX} fps` : `Enter a whole number between ${FPS_MIN} and ${FPS_MAX}.`}
            </span>
          </label>

          <label class="settings-field">
            <span class="settings-field__label">Video bitrate</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="numeric"
              min={BITRATE_MIN}
              max={BITRATE_MAX}
              step={1}
              value={form.bitrateMbps}
              aria-invalid={!bitrateValid}
              onInput={(event) => patch({ bitrateMbps: Math.trunc(Number(event.currentTarget.value)) })}
            />
            <span class={`settings-field__hint${bitrateValid ? "" : " settings-field__hint--error"}`}>
              {bitrateValid ? "Mbps (higher is larger files)" : `Enter a whole number between ${BITRATE_MIN} and ${BITRATE_MAX}.`}
            </span>
          </label>
        </section>

        <section class="settings-section" aria-label="Audio">
          <h2 class="settings-section__title">Audio</h2>

          <label class="settings-toggle">
            <input
              type="checkbox"
              checked={form.gameOnlyAudio}
              onChange={(event) => patch({ gameOnlyAudio: event.currentTarget.checked })}
            />
            <span class="settings-toggle__body">
              <span class="settings-toggle__label">Game-only audio</span>
              <span class="settings-toggle__hint">Capture only Hearthstone's audio where the OS supports it; falls back to system loopback on older builds.</span>
            </span>
          </label>

          <label class="settings-toggle">
            <input
              type="checkbox"
              checked={form.mixMicrophone}
              onChange={(event) => patch({ mixMicrophone: event.currentTarget.checked })}
            />
            <span class="settings-toggle__body">
              <span class="settings-toggle__label">Mix in microphone</span>
              <span class="settings-toggle__hint">Off by default. When on, your microphone is mixed into the recording.</span>
            </span>
          </label>
        </section>

        <section class="settings-section" aria-label="Locations">
          <h2 class="settings-section__title">Locations</h2>
          <p class="settings-note">Managed by BG Recorder. Editing these will arrive in a later update.</p>

          <div class="settings-field settings-field--readonly">
            <span class="settings-field__label">Library folder</span>
            <code class="settings-path">{settings.libraryDir}</code>
          </div>
          <div class="settings-field settings-field--readonly">
            <span class="settings-field__label">Staging folder</span>
            <code class="settings-path">{settings.stagingDir}</code>
          </div>
          <div class="settings-field settings-field--readonly">
            <span class="settings-field__label">Hearthstone install</span>
            <code class="settings-path">{settings.hearthstoneInstallDir ?? "Auto-detected"}</code>
          </div>
        </section>

        <div class="settings-actions">
          <button
            class="button button--quiet"
            type="button"
            disabled={!dirty || saving}
            onClick={() => setForm(formToUpdate(settings))}
          >
            Reset
          </button>
          <button class="button button--primary" type="button" disabled={!canSave} onClick={() => void save()}>
            {saving ? "Saving…" : "Save changes"}
          </button>
        </div>
      </div>
    </main>
  );
}

interface StorageViewProps {
  notify(notice: Notice): void;
}

function round1(value: number): number {
  return Math.round(value * 10) / 10;
}

function bytesToGiB(bytes: number): number {
  return round1(bytes / (1024 ** 3));
}

function giBToBytes(gib: number): number {
  return Math.round(gib * (1024 ** 3));
}

function volumeLabel(volume: StorageVolume, volumes: StorageVolume[]): string {
  if (volume.role === "recording") {
    return "Recording drive";
  }
  const archives = volumes.filter((candidate) => candidate.role === "archive");
  return archives.length > 1 ? `Archive ${archives.indexOf(volume) + 1}` : "Archive drive";
}

function sumBytes(evictions: { sizeBytes: number }[]): number {
  return evictions.reduce((total, eviction) => total + eviction.sizeBytes, 0);
}

/**
 * The Storage surface (M5). Shows live per-volume usage and the eviction preview from the running
 * retention engine, and edits the retention caps. Like recording settings, cap changes are persisted
 * and take effect on the next launch (the running engine captured its options at startup); the preview
 * reflects the CURRENT session so the two can differ until a restart — the note says so.
 */
function StorageView({ notify }: StorageViewProps): JSX.Element {
  const [settings, setSettings] = useState<StorageSettings | null>(null);
  const [preview, setPreview] = useState<StoragePreview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [capGiB, setCapGiB] = useState(0);
  const [reserveGiB, setReserveGiB] = useState(0);
  const [hotSet, setHotSet] = useState(0);
  const [totalCapGiB, setTotalCapGiB] = useState(""); // empty string = no whole-library cap
  // "What would the caps I'm editing do" — fetched while the form is dirty, null otherwise.
  const [proposedPreview, setProposedPreview] = useState<StoragePreview | null>(null);
  const proposedFenceRef = useRef(0);

  const applySettings = (value: StorageSettings): void => {
    setSettings(value);
    setCapGiB(bytesToGiB(value.recordingCapBytes));
    setReserveGiB(bytesToGiB(value.recordingReserveBytes));
    setHotSet(value.hotSetSize);
    setTotalCapGiB(value.totalCapBytes === null ? "" : String(bytesToGiB(value.totalCapBytes)));
  };

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const [loadedSettings, loadedPreview] = await Promise.all([
        bridge.request("storage.get"),
        bridge.request("storage.preview"),
      ]);
      applySettings(loadedSettings);
      setPreview(loadedPreview);
    } catch (err) {
      setError(asErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const totalTrim = totalCapGiB.trim();
  const capValid = Number.isFinite(capGiB) && capGiB >= 1;
  const reserveValid = Number.isFinite(reserveGiB) && reserveGiB >= 0;
  const hotValid = Number.isInteger(hotSet) && hotSet >= 0 && hotSet <= 1000;
  const totalValid = totalTrim === "" || (Number.isFinite(Number(totalTrim)) && Number(totalTrim) >= 1);
  const formValid = capValid && reserveValid && hotValid && totalValid;
  const dirty = settings !== null && (
    capGiB !== bytesToGiB(settings.recordingCapBytes) ||
    reserveGiB !== bytesToGiB(settings.recordingReserveBytes) ||
    hotSet !== settings.hotSetSize ||
    totalTrim !== (settings.totalCapBytes === null ? "" : String(bytesToGiB(settings.totalCapBytes)))
  );
  const canSave = dirty && formValid && !saving;

  // The running engine only reads caps at launch, so this hypothetical preview is the user's ONE
  // accurate look at what edited caps would evict — without it, tighter caps saved today delete
  // recordings at the next start with no warning. Debounced; a stale response is fenced out.
  useEffect(() => {
    if (!dirty || !formValid) {
      proposedFenceRef.current++;
      setProposedPreview(null);
      return;
    }
    const fence = ++proposedFenceRef.current;
    const handle = window.setTimeout(() => {
      void (async () => {
        try {
          const hypothetical = await bridge.request("storage.preview", {
            recordingCapBytes: giBToBytes(capGiB),
            recordingReserveBytes: giBToBytes(reserveGiB),
            hotSetSize: hotSet,
            totalCapBytes: totalCapGiB.trim() === "" ? null : giBToBytes(Number(totalCapGiB.trim())),
          });
          if (proposedFenceRef.current === fence) {
            setProposedPreview(hypothetical);
          }
        } catch {
          // Best-effort: fall back to the in-force preview rather than blocking the editor.
          if (proposedFenceRef.current === fence) {
            setProposedPreview(null);
          }
        }
      })();
    }, 300);
    return () => window.clearTimeout(handle);
  }, [dirty, formValid, capGiB, reserveGiB, hotSet, totalCapGiB]);

  const save = async (): Promise<void> => {
    if (!canSave) {
      return;
    }
    setSaving(true);
    try {
      const update: StorageUpdate = {
        recordingCapBytes: giBToBytes(capGiB),
        recordingReserveBytes: giBToBytes(reserveGiB),
        hotSetSize: hotSet,
        totalCapBytes: totalTrim === "" ? null : giBToBytes(Number(totalTrim)),
      };
      const saved = await bridge.request("storage.set", update);
      applySettings(saved);
      notify({ tone: "success", text: "Storage settings saved — retention changes apply after restart." });
      // Re-read the preview as a SEPARATE best-effort request. Note the native engine captured its caps
      // at startup, so this preview still reflects the IN-FORCE caps (the new ones apply after restart),
      // not the just-saved values — it only picks up live library/free-space changes. Kept separate so a
      // transient preview failure is never misreported as a save failure.
      try {
        setPreview(await bridge.request("storage.preview"));
      } catch {
        // best-effort; the persisted settings are correct regardless of the preview read
      }
    } catch (err) {
      notify({ tone: "error", text: `Could not save storage settings: ${asErrorMessage(err)}` });
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <main class="settings-view">
        <div class="settings-scroll">
          <div class="player-message" style={{ position: "static", margin: "40px auto" }}>
            <span class="spinner" aria-hidden="true" />
            <strong>Loading storage</strong>
          </div>
        </div>
      </main>
    );
  }

  if (error || settings === null || preview === null) {
    return (
      <main class="settings-view">
        <div class="settings-scroll">
          <div class="empty-state">
            <div class="empty-state__glyph" aria-hidden="true">◇</div>
            <strong>Could not load storage</strong>
            {error && <span>{error}</span>}
            <button class="button button--quiet" type="button" onClick={() => void load()}>Try again</button>
          </div>
        </div>
      </main>
    );
  }

  // The retention section shows the caps being edited when the form is dirty (so their consequences
  // are visible BEFORE saving); the usage bars stay on the in-force preview — live physical facts.
  const shownPreview = proposedPreview ?? preview;
  const moveBytes = sumBytes(shownPreview.plannedMoves);
  const deleteBytes = sumBytes(shownPreview.plannedDeletes);
  const nothingPlanned = shownPreview.plannedMoves.length === 0 && shownPreview.plannedDeletes.length === 0;

  return (
    <main class="settings-view">
      <div class="settings-scroll">
        <header class="settings-head">
          <h1>Storage</h1>
          <p>How much space recordings use, and what retention would do to stay within your limits.</p>
        </header>

        <section class="settings-section" aria-label="Usage">
          <h2 class="settings-section__title">Usage</h2>
          {preview.volumes.map((volume, index) => {
            const pct = volume.capBytes > 0 ? Math.min(100, (volume.usedBytes / volume.capBytes) * 100) : 0;
            const over = volume.usedBytes > volume.capBytes;
            return (
              <div class="usage-row" key={`${volume.role}-${index}`}>
                <div class="usage-row__head">
                  <span class="usage-row__label">{volumeLabel(volume, preview.volumes)}</span>
                  {!volume.isOnline && <span class="offline-tag">offline</span>}
                  <span class="mono muted">{formatBytes(volume.usedBytes)} / {formatBytes(volume.capBytes)}</span>
                </div>
                <div class="usage-bar">
                  <div class={`usage-bar__fill${over ? " usage-bar__fill--over" : ""}`} style={{ width: `${pct.toFixed(1)}%` }} />
                </div>
                <div class="usage-row__foot">
                  {volume.matchCount} {volume.matchCount === 1 ? "recording" : "recordings"} · {formatBytes(volume.freeBytes)} free on drive
                </div>
              </div>
            );
          })}
        </section>

        <section class="settings-section" aria-label="Retention preview">
          <h2 class="settings-section__title">Retention preview</h2>
          {proposedPreview !== null && (
            <p class="settings-note">
              Previewing the caps you're editing below — they take effect after a restart. Until then the
              running session keeps enforcing the saved caps.
            </p>
          )}
          {nothingPlanned ? (
            <p class="settings-note">
              {proposedPreview !== null
                ? "Nothing would be cleaned up under these caps."
                : "Nothing to clean up — the library is within its limits."}
            </p>
          ) : (
            <ul class="preview-list">
              {shownPreview.plannedMoves.length > 0 && (
                <li>
                  <strong>{shownPreview.plannedMoves.length}</strong> {shownPreview.plannedMoves.length === 1 ? "recording" : "recordings"} ({formatBytes(moveBytes)}) would be archived to a drive with space.
                </li>
              )}
              {shownPreview.plannedDeletes.length > 0 && (
                <li class="preview-list__danger">
                  <strong>{shownPreview.plannedDeletes.length}</strong> {shownPreview.plannedDeletes.length === 1 ? "recording" : "recordings"} ({formatBytes(deleteBytes)}) would be deleted to stay within the cap. Star a recording to keep it.
                </li>
              )}
            </ul>
          )}
          {shownPreview.recordingBelowFloor && (
            <p class="preview-floor">Free space is below the safety floor — the next match won't be armed until space is freed.</p>
          )}
        </section>

        <section class="settings-section" aria-label="Retention limits">
          <h2 class="settings-section__title">Limits</h2>
          <p class="settings-note">Changes apply after restarting BG Recorder. The current recording always finishes.</p>

          <label class="settings-field">
            <span class="settings-field__label">Recording cap</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="decimal"
              min={1}
              step={1}
              value={capGiB}
              aria-invalid={!capValid}
              onInput={(event) => setCapGiB(Number(event.currentTarget.value))}
            />
            <span class={`settings-field__hint${capValid ? "" : " settings-field__hint--error"}`}>
              {capValid ? "GiB kept on the recording drive" : "Enter at least 1 GiB."}
            </span>
          </label>

          <label class="settings-field">
            <span class="settings-field__label">Free-space floor</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="decimal"
              min={0}
              step={1}
              value={reserveGiB}
              aria-invalid={!reserveValid}
              onInput={(event) => setReserveGiB(Number(event.currentTarget.value))}
            />
            <span class={`settings-field__hint${reserveValid ? "" : " settings-field__hint--error"}`}>
              {reserveValid ? "GiB never filled on the drive" : "Enter 0 or more."}
            </span>
          </label>

          <label class="settings-field">
            <span class="settings-field__label">Always keep newest</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="numeric"
              min={0}
              max={1000}
              step={1}
              value={hotSet}
              aria-invalid={!hotValid}
              onInput={(event) => setHotSet(Math.trunc(Number(event.currentTarget.value)))}
            />
            <span class={`settings-field__hint${hotValid ? "" : " settings-field__hint--error"}`}>
              {hotValid ? "recordings pinned to the recording drive" : "Enter 0–1000."}
            </span>
          </label>

          <label class="settings-field">
            <span class="settings-field__label">Whole-library cap</span>
            <input
              class="settings-input mono"
              type="number"
              inputMode="decimal"
              min={1}
              step={1}
              value={totalCapGiB}
              placeholder="None"
              aria-invalid={!totalValid}
              onInput={(event) => setTotalCapGiB(event.currentTarget.value)}
            />
            <span class={`settings-field__hint${totalValid ? "" : " settings-field__hint--error"}`}>
              {totalValid ? "GiB across all drives — leave blank for no cap" : "Enter at least 1 GiB, or leave blank."}
            </span>
          </label>
        </section>

        <section class="settings-section" aria-label="Archive drives">
          <h2 class="settings-section__title">Archive drives</h2>
          {settings.archiveVolumes.length === 0 ? (
            <p class="settings-note">No archive drives configured. Overflow recordings are deleted oldest-first once the cap is reached.</p>
          ) : (
            settings.archiveVolumes.map((archive, index) => (
              <div class="settings-field settings-field--readonly" key={`${archive.directory}-${index}`}>
                <span class="settings-field__label">Archive {index + 1}</span>
                <code class="settings-path">{archive.directory} · cap {formatBytes(archive.capBytes)}</code>
              </div>
            ))
          )}
        </section>

        <div class="settings-actions">
          <button class="button button--quiet" type="button" disabled={!dirty || saving} onClick={() => applySettings(settings)}>
            Reset
          </button>
          <button class="button button--primary" type="button" disabled={!canSave} onClick={() => void save()}>
            {saving ? "Saving…" : "Save changes"}
          </button>
        </div>
      </div>
    </main>
  );
}

export function App(): JSX.Element {
  const [view, setView] = useState<View>("library");
  const [matches, setMatches] = useState<MatchSummary[]>([]);
  const [coordinatorState, setCoordinatorState] = useState<CoordinatorState>("gameNotFound");
  const [bucket, setBucket] = useState<Bucket>("all");
  const [segment, setSegment] = useState<Segment>("all");
  const [dateFilter, setDateFilter] = useState<DateFilter>("30d");
  const [search, setSearch] = useState("");
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<MatchDetailResult | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [detailVersion, setDetailVersion] = useState(0);
  const [listLoading, setListLoading] = useState(true);
  const [listLoaded, setListLoaded] = useState(false);
  const [listError, setListError] = useState<string | null>(null);
  const [starPending, setStarPending] = useState<ReadonlySet<number>>(new Set());
  const [ratingPending, setRatingPending] = useState<ReadonlySet<number>>(new Set());
  const [deletePending, setDeletePending] = useState<ReadonlySet<number>>(new Set());
  const [ratingHealth, setRatingHealth] = useState<RatingHealth | null>(null);
  const [pendingCommand, setPendingCommand] = useState<RecorderCommand | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const coordinatorStateRef = useRef<CoordinatorState>(coordinatorState);
  const coordinatorNotificationVersionRef = useRef(0);
  const listRequestVersionRef = useRef(0);
  const mutationVersionRef = useRef(0);
  const starMutationsRef = useRef(new Map<number, StarMutation>());
  const ratingMutationsRef = useRef(new Map<number, RatingMutation>());
  const outstandingFencesRef = useRef<ReadFences[]>([]);
  // Ids removed via delete. A library.list already in flight (or a later refresh) must not resurrect a
  // deleted row; deletion is permanent, so these are filtered out of every list result for the session.
  const deletedIdsRef = useRef(new Set<number>());
  // The selected recording's <video>, shared with the Player so keyboard shortcuts can drive playback.
  const videoRef = useRef<HTMLVideoElement | null>(null);

  // A fenced async read (library.list / library.get) registers one fence per optimistic field so
  // committed history is retained until every read that could still be racing it has settled, then
  // pruned. Star and rating share one monotonic mutation-version clock but track separate maps.
  const beginReadFences = useCallback((): ReadFences => {
    const fences: ReadFences = {
      star: createReadFence(mutationVersionRef.current, starMutationsRef.current),
      rating: createReadFence(mutationVersionRef.current, ratingMutationsRef.current),
    };
    outstandingFencesRef.current.push(fences);
    return fences;
  }, []);

  const endReadFences = useCallback((fences: ReadFences): void => {
    const list = outstandingFencesRef.current;
    const index = list.indexOf(fences);
    if (index >= 0) {
      list.splice(index, 1);
    }
    pruneMutations(starMutationsRef.current, list.map((entry) => entry.star));
    pruneMutations(ratingMutationsRef.current, list.map((entry) => entry.rating));
  }, []);

  // Applies both field guards so a slower list/detail read cannot overwrite a newer optimistic edit.
  const protectMatch = useCallback((match: MatchSummary, fences: ReadFences): MatchSummary =>
    protectManualRatingFromStaleRead(
      protectStarredFromStaleRead(match, fences.star, starMutationsRef.current),
      fences.rating,
      ratingMutationsRef.current,
    ), []);

  const loadLibrary = useCallback(async (): Promise<void> => {
    const requestVersion = ++listRequestVersionRef.current;
    const coordinatorNotificationVersion = coordinatorNotificationVersionRef.current;
    const fences = beginReadFences();
    setListLoading(true);
    setListError(null);
    try {
      const result = await bridge.request("library.list");
      if (requestVersion !== listRequestVersionRef.current) {
        return;
      }

      setMatches(result.matches
        .filter((match) => !deletedIdsRef.current.has(match.id))
        .map((match) => protectMatch(match, fences)));
      setListLoaded(true);

      // A recorder notification is newer than the state bundled into any list request that was
      // already in flight. Applying that snapshot could erase Finalizing and prevent the later
      // ready-state notification from triggering the post-commit library refresh.
      if (isCoordinatorSnapshotCurrent(
        coordinatorNotificationVersion,
        coordinatorNotificationVersionRef.current,
      )) {
        const state = normalizeCoordinatorState(result.coordinatorState);
        const previousState = coordinatorStateRef.current;
        coordinatorStateRef.current = state;
        setCoordinatorState(state);

        // ListLibrary reads rows before it snapshots coordinator state. If finalization commits in
        // between those operations, this response can say "armed" while still carrying old rows;
        // a follow-up read begun after the observed transition is guaranteed to see the commit.
        if (shouldReloadLibraryAfterStateChange(previousState, state)) {
          void loadLibrary();
        }
      }
    } catch (error) {
      if (requestVersion === listRequestVersionRef.current) {
        setListError(asErrorMessage(error));
        setListLoaded(true);
      }
    } finally {
      endReadFences(fences);
      if (requestVersion === listRequestVersionRef.current) {
        setListLoading(false);
      }
    }
  }, [beginReadFences, endReadFences, protectMatch]);

  // A coordinator transition observed locally (a command reply or a native notification) is newer
  // than the coordinatorState bundled into any library.list already in flight. Bumping the
  // notification version invalidates that in-flight snapshot so it cannot overwrite the state
  // applied here. Every path that applies a coordinator transition must go through this.
  const commitCoordinatorState = useCallback((state: CoordinatorState): void => {
    coordinatorNotificationVersionRef.current += 1;
    coordinatorStateRef.current = state;
    setCoordinatorState(state);
  }, []);

  useEffect(() => {
    void loadLibrary();
    return bridge.on("recorder.stateChanged", ({ state }) => {
      const previousState = coordinatorStateRef.current;
      const nextState = normalizeCoordinatorState(state);
      commitCoordinatorState(nextState);

      if (shouldReloadLibraryAfterStateChange(previousState, nextState)) {
        void loadLibrary();
      }
    });
  }, [loadLibrary, commitCoordinatorState]);

  useEffect(() => {
    if (!notice) {
      return;
    }
    const timer = window.setTimeout(() => setNotice(null), 4_000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const visibleMatches = useMemo(() => {
    const query = search.trim().toLowerCase();
    return matches.filter((match) => {
      const gameType = normalizeGameType(match.gameType);
      if (bucket === "starred" ? !match.starred : bucket !== "all" && gameType !== bucket) {
        return false;
      }
      if (segment !== "all") {
        if (match.place === null) {
          return false;
        }
        const top = match.place <= topThreshold(match);
        if ((segment === "top" && !top) || (segment === "bottom" && top)) {
          return false;
        }
      }
      if (query && !`${match.heroCardId ?? ""} ${match.id}`.toLowerCase().includes(query)) {
        return false;
      }
      return isInDateRange(match, dateFilter);
    });
  }, [bucket, dateFilter, matches, search, segment]);

  useEffect(() => {
    if (visibleMatches.length === 0) {
      if (selectedId !== null) {
        setSelectedId(null);
      }
      return;
    }
    if (selectedId === null || !visibleMatches.some((match) => match.id === selectedId)) {
      setSelectedId(visibleMatches[0].id);
    }
  }, [selectedId, visibleMatches]);

  useEffect(() => {
    if (selectedId === null) {
      setDetail(null);
      setDetailError(null);
      setDetailLoading(false);
      return;
    }

    let cancelled = false;
    setDetail(null);
    setDetailError(null);
    setDetailLoading(true);
    const fences = beginReadFences();

    void bridge.request("library.get", { matchId: selectedId })
      .then((result) => {
        if (!cancelled) {
          const protectedMatch = protectMatch(result.match, fences);
          setDetail({ ...result, match: protectedMatch });
          setMatches((current) => current.map((match) =>
            match.id === result.match.id ? protectedMatch : match));
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setDetailError(asErrorMessage(error));
        }
      })
      .finally(() => {
        endReadFences(fences);
        if (!cancelled) {
          setDetailLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [detailVersion, selectedId, beginReadFences, endReadFences, protectMatch]);

  const ratingMode: GameType = bucket === "duos" ? "duos" : "solo";
  useEffect(() => {
    let cancelled = false;
    void bridge.request("rating.get", { mode: ratingMode })
      .then((info) => {
        if (!cancelled) {
          setRatingHealth(info.health);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setRatingHealth(null);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [ratingMode]);

  // Global match-list navigation. Arrow/Home/End move the selection in the library view; playback keys
  // (Space, ←/→, [ ]) are owned by the Player so seeks route through its clip-clearing seek. Typing
  // targets are always left alone.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const target = document.activeElement;
      const tag = target?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || (target as HTMLElement | null)?.isContentEditable) {
        return;
      }
      // When the video is focused, its native controls own the arrow keys (volume/seek) — don't hijack
      // them to change the library selection, which would yank playback onto a different recording.
      if (tag === "VIDEO") {
        return;
      }
      if (view !== "library") {
        return;
      }

      const ids = visibleMatches.map((match) => match.id);
      if (ids.length === 0) {
        return;
      }
      const index = selectedId === null ? -1 : ids.indexOf(selectedId);
      if (event.key === "ArrowDown") {
        event.preventDefault();
        setSelectedId(ids[Math.min(ids.length - 1, index + 1)] ?? ids[0]);
      } else if (event.key === "ArrowUp") {
        event.preventDefault();
        setSelectedId(index <= 0 ? ids[0] : ids[index - 1]);
      } else if (event.key === "Home") {
        event.preventDefault();
        setSelectedId(ids[0]);
      } else if (event.key === "End") {
        event.preventDefault();
        setSelectedId(ids[ids.length - 1]);
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [view, visibleMatches, selectedId]);

  const selectedMatch = matches.find((match) => match.id === selectedId) ?? null;
  const soloCount = matches.filter((match) => normalizeGameType(match.gameType) === "solo").length;
  const duosCount = matches.filter((match) => normalizeGameType(match.gameType) === "duos").length;
  const starredCount = matches.filter((match) => match.starred).length;
  const topLabel = bucket === "duos" ? "Top 2" : bucket === "solo" ? "Top 4" : "Top finish";
  const bottomLabel = bucket === "duos" ? "Bottom 2" : bucket === "solo" ? "Bottom 4" : "Bottom finish";

  const handleCommand = async (command: RecorderCommand): Promise<void> => {
    setPendingCommand(command);
    try {
      switch (command) {
        case "recorder.stop": {
          const state = normalizeCoordinatorState((await bridge.request("recorder.stop")).state);
          commitCoordinatorState(state);
          await loadLibrary();
          setNotice({ tone: "success", text: "Recording finalized." });
          break;
        }
        case "recorder.pause": {
          const state = normalizeCoordinatorState((await bridge.request("recorder.pause")).state);
          commitCoordinatorState(state);
          setNotice({ tone: "success", text: "Auto-recording paused." });
          break;
        }
        case "recorder.resume": {
          const state = normalizeCoordinatorState((await bridge.request("recorder.resume")).state);
          commitCoordinatorState(state);
          setNotice({ tone: "success", text: "Auto-recording resumed." });
          break;
        }
      }
    } catch (error) {
      setNotice({ tone: "error", text: asErrorMessage(error) });
    } finally {
      setPendingCommand(null);
    }
  };

  const handleToggleStar = async (match: MatchSummary): Promise<void> => {
    if (starMutationsRef.current.get(match.id)?.pending) {
      return;
    }

    const starred = !match.starred;
    const mutationVersion = ++mutationVersionRef.current;
    starMutationsRef.current.set(match.id, {
      version: mutationVersion,
      starred,
      pending: true,
    });
    setStarPending((current) => new Set(current).add(match.id));
    setMatches((current) => current.map((candidate) => candidate.id === match.id ? { ...candidate, starred } : candidate));
    setDetail((current) => current?.match.id === match.id
      ? { ...current, match: { ...current.match, starred } }
      : current);

    try {
      const result = await bridge.request("library.setStarred", { matchId: match.id, starred });
      const confirmedStarred = result?.starred ?? starred;
      const mutation = starMutationsRef.current.get(match.id);
      if (mutation?.version === mutationVersion) {
        starMutationsRef.current.set(match.id, {
          version: mutationVersion,
          starred: confirmedStarred,
          pending: false,
        });
        setMatches((current) => current.map((candidate) => candidate.id === match.id
          ? { ...candidate, starred: confirmedStarred }
          : candidate));
        setDetail((current) => current?.match.id === match.id
          ? { ...current, match: { ...current.match, starred: confirmedStarred } }
          : current);
      }
    } catch (error) {
      const mutation = starMutationsRef.current.get(match.id);
      if (mutation?.version === mutationVersion) {
        // Keep the rollback as the latest completed mutation. A much older list/detail read may
        // still return after this failed attempt and must not erase an earlier successful toggle.
        starMutationsRef.current.set(match.id, {
          version: mutationVersion,
          starred: match.starred,
          pending: false,
        });
        setMatches((current) => current.map((candidate) => candidate.id === match.id
          ? { ...candidate, starred: match.starred }
          : candidate));
        setDetail((current) => current?.match.id === match.id
          ? { ...current, match: { ...current.match, starred: match.starred } }
          : current);
      }
      setNotice({ tone: "error", text: `Could not update starred state: ${asErrorMessage(error)}` });
    } finally {
      if (!starMutationsRef.current.get(match.id)?.pending) {
        setStarPending((current) => {
          const next = new Set(current);
          next.delete(match.id);
          return next;
        });
      }
    }
  };

  const handleSetManualRating = async (match: MatchSummary, rating: number | null): Promise<void> => {
    if (ratingMutationsRef.current.get(match.id)?.pending || rating === match.manualRating) {
      return;
    }

    const previous = match.manualRating;
    const mutationVersion = ++mutationVersionRef.current;
    ratingMutationsRef.current.set(match.id, { version: mutationVersion, rating, pending: true });
    setRatingPending((current) => new Set(current).add(match.id));
    setMatches((current) => current.map((candidate) => candidate.id === match.id
      ? { ...candidate, manualRating: rating }
      : candidate));
    setDetail((current) => current?.match.id === match.id
      ? { ...current, match: { ...current.match, manualRating: rating } }
      : current);

    try {
      const result = await bridge.request("library.setManualRating", { matchId: match.id, rating });
      const confirmed = result?.rating ?? rating;
      const mutation = ratingMutationsRef.current.get(match.id);
      if (mutation?.version === mutationVersion) {
        ratingMutationsRef.current.set(match.id, { version: mutationVersion, rating: confirmed, pending: false });
        setMatches((current) => current.map((candidate) => candidate.id === match.id
          ? { ...candidate, manualRating: confirmed }
          : candidate));
        setDetail((current) => current?.match.id === match.id
          ? { ...current, match: { ...current.match, manualRating: confirmed } }
          : current);
      }
    } catch (error) {
      const mutation = ratingMutationsRef.current.get(match.id);
      if (mutation?.version === mutationVersion) {
        // Roll back to the pre-edit value as the latest completed mutation so a much older list or
        // detail read that returns afterward cannot resurrect the rejected value.
        ratingMutationsRef.current.set(match.id, { version: mutationVersion, rating: previous, pending: false });
        setMatches((current) => current.map((candidate) => candidate.id === match.id
          ? { ...candidate, manualRating: previous }
          : candidate));
        setDetail((current) => current?.match.id === match.id
          ? { ...current, match: { ...current.match, manualRating: previous } }
          : current);
      }
      setNotice({ tone: "error", text: `Could not update rating: ${asErrorMessage(error)}` });
    } finally {
      if (!ratingMutationsRef.current.get(match.id)?.pending) {
        setRatingPending((current) => {
          const next = new Set(current);
          next.delete(match.id);
          return next;
        });
      }
    }
  };

  const handleDelete = async (match: MatchSummary): Promise<void> => {
    if (deletePending.has(match.id)) {
      return;
    }
    setDeletePending((current) => new Set(current).add(match.id));
    try {
      await bridge.request("library.delete", { matchId: match.id });
      deletedIdsRef.current.add(match.id);
      setMatches((current) => current.filter((candidate) => candidate.id !== match.id));
      setDetail((current) => (current?.match.id === match.id ? null : current));
      setNotice({ tone: "success", text: "Recording deleted." });
    } catch (error) {
      setNotice({ tone: "error", text: `Could not delete recording: ${asErrorMessage(error)}` });
    } finally {
      setDeletePending((current) => {
        const next = new Set(current);
        next.delete(match.id);
        return next;
      });
    }
  };

  const clearFilters = (): void => {
    setSearch("");
    setSegment("all");
    setDateFilter("all");
  };

  // The library failing to open is fatal only for the library view; Settings stays reachable so the
  // user can still inspect and change preferences.
  if (view === "library" && listLoaded && listError && matches.length === 0) {
    return (
      <main class="fatal-state">
        <div class="brand-mark" aria-hidden="true"><span /></div>
        <p class="eyebrow">BATTLEGROUNDS RECORDER</p>
        <h1>Could not open the library</h1>
        <p>{listError}</p>
        <div class="fatal-state__actions">
          <button class="button button--primary" type="button" disabled={listLoading} onClick={() => void loadLibrary()}>
            {listLoading ? "Retrying…" : "Try again"}
          </button>
          <button class="button button--quiet" type="button" onClick={() => setView("settings")}>Open settings</button>
        </div>
      </main>
    );
  }

  return (
    <div class="app-shell">
      <header class="app-header">
        <div class="app-brand">
          <div class="brand-mark" aria-hidden="true"><span /></div>
          <div>
            <strong>Battlegrounds Recorder</strong>
            <span>Local match library</span>
          </div>
        </div>
        <nav class="view-tabs" aria-label="Views">
          <button
            class={`view-tab${view === "library" ? " view-tab--active" : ""}`}
            type="button"
            aria-pressed={view === "library"}
            onClick={() => setView("library")}
          >
            Library
          </button>
          <button
            class={`view-tab${view === "storage" ? " view-tab--active" : ""}`}
            type="button"
            aria-pressed={view === "storage"}
            onClick={() => setView("storage")}
          >
            Storage
          </button>
          <button
            class={`view-tab${view === "settings" ? " view-tab--active" : ""}`}
            type="button"
            aria-pressed={view === "settings"}
            onClick={() => setView("settings")}
          >
            Settings
          </button>
        </nav>

        <div class="app-header__actions">
          {bridge.mode === "mock" && <span class="environment-badge">Browser preview</span>}
          {view === "library" && listError && matches.length > 0 && <span class="refresh-warning">Refresh failed</span>}
          {view === "library" && (
            <button
              class="icon-button"
              type="button"
              title="Refresh library"
              aria-label="Refresh library"
              disabled={listLoading}
              onClick={() => void loadLibrary()}
            >
              <span class={listLoading ? "refresh-icon refresh-icon--active" : "refresh-icon"} aria-hidden="true">↻</span>
            </button>
          )}
        </div>
      </header>

      {view === "settings" ? (
        <SettingsView notify={setNotice} />
      ) : view === "storage" ? (
        <StorageView notify={setNotice} />
      ) : (
      <div class="app-layout">
        <aside class="sidebar">
          <StatusCard
            state={coordinatorState}
            pendingCommand={pendingCommand}
            onCommand={(command) => void handleCommand(command)}
          />

          <nav class="library-nav" aria-label="Library buckets">
            <p class="nav-heading">LOBBIES</p>
            <BucketButton active={bucket === "all"} count={matches.length} label="All" onClick={() => setBucket("all")} />
            <BucketButton active={bucket === "solo"} count={soloCount} label="Solo" onClick={() => setBucket("solo")} />
            <BucketButton active={bucket === "duos"} count={duosCount} label="Duos" onClick={() => setBucket("duos")} />
            <p class="nav-heading nav-heading--secondary">LIBRARY</p>
            <BucketButton active={bucket === "starred"} count={starredCount} label="Starred" onClick={() => setBucket("starred")} />
          </nav>

          <RatingCard matches={matches} bucket={bucket} health={ratingHealth} />

          <div class="sidebar-note">
            <strong>Local by design</strong>
            <span>Recordings and metadata stay on this PC.</span>
          </div>
        </aside>

        <main class="library-main">
          <Player
            match={selectedMatch}
            detail={detail}
            loading={detailLoading}
            error={detailError}
            ratingPending={selectedMatch !== null && ratingPending.has(selectedMatch.id)}
            videoRef={videoRef}
            onRetry={() => setDetailVersion((value) => value + 1)}
            onSetRating={(match, rating) => void handleSetManualRating(match, rating)}
          />

          <section class="library-panel" aria-label="Recordings">
            <div class="filter-bar">
              <div class="segment-control" aria-label="Placement filter">
                {(["all", "top", "bottom"] as const).map((value) => {
                  const label = value === "all" ? "All" : value === "top" ? topLabel : bottomLabel;
                  return (
                    <button
                      key={value}
                      class={segment === value ? "segment-button segment-button--active" : "segment-button"}
                      type="button"
                      aria-pressed={segment === value}
                      onClick={() => setSegment(value)}
                    >
                      {label}
                    </button>
                  );
                })}
              </div>

              <label class="search-field">
                <span class="visually-hidden">Search recordings</span>
                <span class="search-field__icon" aria-hidden="true">⌕</span>
                <input
                  type="search"
                  value={search}
                  placeholder="Search hero or match ID…"
                  onInput={(event) => setSearch(event.currentTarget.value)}
                />
              </label>

              <label class="date-field">
                <span class="visually-hidden">Date range</span>
                <select value={dateFilter} onChange={(event) => setDateFilter(event.currentTarget.value as DateFilter)}>
                  {dateOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </select>
              </label>
            </div>

            {visibleMatches.length === 0 && !listLoading ? (
              <div class="empty-state">
                <div class="empty-state__glyph" aria-hidden="true">◇</div>
                <strong>{matches.length === 0 ? "No recordings yet" : "Nothing matches these filters"}</strong>
                <span>{matches.length === 0 ? "Your next completed Battlegrounds match will appear here." : "Try another library bucket or clear the current filters."}</span>
                {matches.length > 0 && <button class="button button--quiet" type="button" onClick={clearFilters}>Clear filters</button>}
              </div>
            ) : (
              <MatchTable
                matches={visibleMatches}
                selectedId={selectedId}
                loading={!listLoaded && listLoading}
                starPending={starPending}
                deletePending={deletePending}
                onSelect={setSelectedId}
                onToggleStar={(match) => void handleToggleStar(match)}
                onDelete={(match) => void handleDelete(match)}
              />
            )}
          </section>
        </main>
      </div>
      )}

      {notice && <div class={`toast toast--${notice.tone}`} role="status">{notice.text}</div>}
    </div>
  );
}
