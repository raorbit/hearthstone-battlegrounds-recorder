import { type JSX } from "preact";
import { useCallback, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { bridge } from "./bridge";
import {
  type CoordinatorState,
  type Marker,
  type MatchDetailResult,
  type MatchSummary,
  normalizeCoordinatorState,
  normalizeGameType,
  normalizeMarkerKind,
  normalizeVideoStatus,
} from "./types";

type Bucket = "all" | "solo" | "duos" | "starred";
type Segment = "all" | "top" | "bottom";
type DateFilter = "all" | "today" | "7d" | "30d" | "90d";
type RecorderCommand = "recorder.stop" | "recorder.pause" | "recorder.resume";
type Notice = { tone: "success" | "error"; text: string };

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

interface PlayerProps {
  match: MatchSummary | null;
  detail: MatchDetailResult | null;
  loading: boolean;
  error: string | null;
  onRetry(): void;
}

function Player({ match, detail, loading, error, onRetry }: PlayerProps): JSX.Element {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [currentTime, setCurrentTime] = useState(0);
  const [mediaDuration, setMediaDuration] = useState(0);
  const [mediaError, setMediaError] = useState<string | null>(null);
  const activeMatch = detail?.match ?? match;

  useEffect(() => {
    setCurrentTime(0);
    setMediaDuration(0);
    setMediaError(null);
  }, [activeMatch?.id, activeMatch?.mediaUrl]);

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

  const handleTimelineClick = (event: JSX.TargetedMouseEvent<HTMLDivElement>): void => {
    if (durationMs <= 0) {
      return;
    }
    const bounds = event.currentTarget.getBoundingClientRect();
    const ratio = Math.min(1, Math.max(0, (event.clientX - bounds.left) / bounds.width));
    seekTo(durationMs * ratio);
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
          controls
          preload="metadata"
          onLoadedMetadata={(event) => {
            setMediaDuration(Number.isFinite(event.currentTarget.duration) ? event.currentTarget.duration : 0);
            setMediaError(null);
          }}
          onTimeUpdate={(event) => setCurrentTime(event.currentTarget.currentTime)}
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
                  seekTo(marker.atMs);
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
    </section>
  );
}

interface MatchTableProps {
  matches: MatchSummary[];
  selectedId: number | null;
  loading: boolean;
  starPending: ReadonlySet<number>;
  onSelect(matchId: number): void;
  onToggleStar(match: MatchSummary): void;
}

function MatchTable({ matches, selectedId, loading, starPending, onSelect, onToggleStar }: MatchTableProps): JSX.Element {
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
            <th><span class="visually-hidden">Starred</span></th>
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
                <td><span class={`video-status video-status--${status}`}>{status}</span></td>
                <td>
                  <button
                    class={`star-button${match.starred ? " star-button--active" : ""}`}
                    type="button"
                    title={match.starred ? "Remove from starred" : "Keep this recording"}
                    aria-label={match.starred ? "Remove from starred" : "Keep this recording"}
                    aria-pressed={match.starred}
                    disabled={starPending.has(match.id)}
                    onClick={(event) => {
                      event.stopPropagation();
                      onToggleStar(match);
                    }}
                  >
                    {starPending.has(match.id) ? <span class="spinner spinner--small" /> : "★"}
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

export function App(): JSX.Element {
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
  const [pendingCommand, setPendingCommand] = useState<RecorderCommand | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);

  // Mirror the pending-star set into a ref so the detail-fetch effect can consult it without
  // taking a dependency on it (which would re-run the fetch on every star toggle).
  const starPendingRef = useRef(starPending);
  starPendingRef.current = starPending;

  const loadLibrary = useCallback(async (): Promise<void> => {
    setListLoading(true);
    setListError(null);
    try {
      const result = await bridge.request("library.list");
      setMatches(result.matches);
      setCoordinatorState(normalizeCoordinatorState(result.coordinatorState));
      setListLoaded(true);
    } catch (error) {
      setListError(asErrorMessage(error));
      setListLoaded(true);
    } finally {
      setListLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadLibrary();
    return bridge.on("recorder.stateChanged", ({ state }) => {
      setCoordinatorState(normalizeCoordinatorState(state));
    });
  }, [loadLibrary]);

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

    void bridge.request("library.get", { matchId: selectedId })
      .then((result) => {
        if (!cancelled) {
          // A star toggle does not change selectedId/detailVersion, so this fetch is not cancelled
          // when the user stars the row mid-load. If a star mutation for this id is still pending,
          // keep the optimistic `starred` instead of the pre-toggle snapshot this response carries.
          const starLocked = starPendingRef.current.has(result.match.id);
          setDetail((currentDetail) =>
            starLocked && currentDetail?.match.id === result.match.id
              ? { ...result, match: { ...result.match, starred: currentDetail.match.starred } }
              : result);
          setMatches((current) => current.map((match) => {
            if (match.id !== result.match.id) {
              return match;
            }
            return starLocked ? { ...result.match, starred: match.starred } : result.match;
          }));
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setDetailError(asErrorMessage(error));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setDetailLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [detailVersion, selectedId]);

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
        case "recorder.stop":
          setCoordinatorState(normalizeCoordinatorState((await bridge.request("recorder.stop")).state));
          setNotice({ tone: "success", text: "Recording finalized." });
          break;
        case "recorder.pause":
          setCoordinatorState(normalizeCoordinatorState((await bridge.request("recorder.pause")).state));
          setNotice({ tone: "success", text: "Auto-recording paused." });
          break;
        case "recorder.resume":
          setCoordinatorState(normalizeCoordinatorState((await bridge.request("recorder.resume")).state));
          setNotice({ tone: "success", text: "Auto-recording resumed." });
          break;
      }
    } catch (error) {
      setNotice({ tone: "error", text: asErrorMessage(error) });
    } finally {
      setPendingCommand(null);
    }
  };

  const handleToggleStar = async (match: MatchSummary): Promise<void> => {
    const starred = !match.starred;
    setStarPending((current) => new Set(current).add(match.id));
    setMatches((current) => current.map((candidate) => candidate.id === match.id ? { ...candidate, starred } : candidate));
    setDetail((current) => current?.match.id === match.id
      ? { ...current, match: { ...current.match, starred } }
      : current);

    try {
      await bridge.request("library.setStarred", { matchId: match.id, starred });
    } catch (error) {
      setMatches((current) => current.map((candidate) => candidate.id === match.id ? { ...candidate, starred: match.starred } : candidate));
      setDetail((current) => current?.match.id === match.id
        ? { ...current, match: { ...current.match, starred: match.starred } }
        : current);
      setNotice({ tone: "error", text: `Could not update starred state: ${asErrorMessage(error)}` });
    } finally {
      setStarPending((current) => {
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

  if (listLoaded && listError && matches.length === 0) {
    return (
      <main class="fatal-state">
        <div class="brand-mark" aria-hidden="true"><span /></div>
        <p class="eyebrow">BATTLEGROUNDS RECORDER</p>
        <h1>Could not open the library</h1>
        <p>{listError}</p>
        <button class="button button--primary" type="button" disabled={listLoading} onClick={() => void loadLibrary()}>
          {listLoading ? "Retrying…" : "Try again"}
        </button>
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
        <div class="app-header__actions">
          {bridge.mode === "mock" && <span class="environment-badge">Browser preview</span>}
          {listError && matches.length > 0 && <span class="refresh-warning">Refresh failed</span>}
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
        </div>
      </header>

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
            onRetry={() => setDetailVersion((value) => value + 1)}
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
                onSelect={setSelectedId}
                onToggleStar={(match) => void handleToggleStar(match)}
              />
            )}
          </section>
        </main>
      </div>

      {notice && <div class={`toast toast--${notice.tone}`} role="status">{notice.text}</div>}
    </div>
  );
}
