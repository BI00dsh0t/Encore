# Retain calculated video-track thumbnails

Builder request, 2026-11-07: “the video thumbnails in the video track should be cached after being calculated.” Ordinary implementation on `feature/clip-thumbnail-cache`.

Integrated into develop: feature `4ad1d08`; no-ff merge `8cd6b9a495dc443f4a2cec78125eaf9b82df657a`.

The previous editor kept frozen thumbnails in memory only until its creator closed. Extend retention across creator reopenings and app restarts without changing the existing worker's thumbnail generation, display/time mapping or playback loop.

- App startup injects the current instance's local `cache/clip-thumbnails/v1` root through MainViewModel → LibraryPage → ClipCreatorPage. No installed/Dev/test instance shares a cache root. Tests without an injected root retain temporary-only behavior and never access the real instance's cache.
- Hash source absolute path, length, UTC modification ticks, duration and thumbnail-policy version. Check source identity before/after a disk hit. Decode cached JPEGs off-thread through the same bounded, detached/frozen bitmap loader. A valid cache hit never calls `prepareThumbnails`; seek/trim/zoom/pan continue reusing memory.
- After calculating/validating new frames, retain JPEGs and a bounded source-bound manifest in a same-root staging folder, then atomically rename it into a cache entry. Keep worker scratch cleanup after all reads/copies complete. Cancelled/failed preparation cannot publish a partial cache entry.
- Absent/stale/corrupt caches are misses and are recalculated. Cache IO failures preserve the ready in-memory filmstrip and editing/export behavior. No permanent routine loading text or settings changes.
- Bound completed entries to 128 / 256 MiB using recency eviction; clean old abandoned staging folders. Validate every deletion as an immediate owned cache child; skip reparse directories and nested directory trees. Do not delete source media or unrelated files/folders.

Verification scope: zero-warning Release build and authorized non-Explicit ClipCreatorTests only, with injected temporary cache roots. Reuse existing regression suite plus focused persistent reuse/source invalidation, cancellation/corrupt-cache recovery and eviction ownership checks. Broader shell suites, actual-media extraction/performance, Heavy, HITL, publish and Dev App work remain deferred. No push.

Verification result: final Release build **0 warnings/errors**; authorized non-Explicit ClipCreatorTests **25 passed, 0 failed/skipped**. Scoped ordinary review found no unresolved directly introduced issue. Real instance data/caches, Dev App, Library and settings were untouched.
