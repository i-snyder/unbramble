# UnBramble — Architecture

Agent-facing reference: the settled design, the invariants that must never be silently traded away, and the correctness findings confirmed against real Unity output and adversarial review. This is the doc to read before touching the scanner, parser, store, or liveness code. History of how these decisions were reached lives in git history; this doc states only what's true today.

## What this is

UnBramble is a standalone Windows CLI (`unbramble`) that builds a unified dependency graph of a Unity project — GUID/asset references (prefabs, scenes, materials, shaders, UI Toolkit, Addressables, …) plus C# semantic analysis (Roslyn) — so an AI coding agent (or a human) can ask "what does this touch / what touches this" across the static reference forms UnBramble models instead of reconstructing the picture by grepping and manual reading.

## Non-negotiable invariants

- **Correctness comes first.** Never trade it for speed, cost, or token efficiency.
- **Risk is asymmetric.** A false deletion claim can break a game; "can't tell" only costs another look. Ambiguity may downgrade confidence, never upgrade it.
- **Freshness invariant: never wrong, only sometimes slower.** The index may be slow to answer; it may never silently answer from stale data.
- **One installable tool.** `unbramble` is self-contained and doesn't depend on an IDE or separate analyzer.
- **Not just deletion.** Refactoring needs the same dependency awareness.

## Runtime boundaries

Rider, ReSharper, `resharper-unity`, and CodeGraph were evaluated as references but aren't runtime dependencies. UnBramble uses bundled Roslyn because its C# graph requires semantic type, overload, and generic resolution. Licenses are recorded in `THIRD-PARTY-NOTICES.md`.

## Unity serialization primer (facts about Unity, not design choices)

- Every asset (and folder) under `Assets/` and embedded packages has a sibling `<name>.meta` containing `guid: <32 lowercase hex>` — the asset's identity.
- Text-serialized assets are Unity YAML: multi-document files with headers `--- !u!<ClassID> &<fileID>` (fileIDs can be negative; a real, common variant has a trailing ` stripped` token for stripped prefab-instance overrides — a parser that doesn't tolerate it misattributes every ref in those documents).
- A cross-asset reference looks like `{fileID: 2100000, guid: 8b5e4f9c…, type: 2}`. References without `guid:` are internal to the same file.
- Script attachment: `m_Script: {fileID: 11500000, guid: <guid of Foo.cs.meta>, type: 3}`.
- A component's owning GameObject is a same-file ref `m_GameObject: {fileID: G}`; a GameObject's name is `m_Name:` in its own `!u!1` document. Together these let refs be annotated with human-readable GameObject names.
- UnityEvent persistent calls serialize the bound method name in plain text next to the target ref (`m_Target: {…}` + `m_MethodName: Jump` in the same `m_Calls` entry). **`m_Target` never references a MonoScript** — for a cross-asset binding it's `{fileID: <component>, guid: <containing asset — a prefab/scene>}`; for the common same-asset case (button and handler in one prefab) it's a bare fileID with **no guid at all**. Type selection for the bound method can only come from the separate `m_TargetAssemblyTypeName: Foo, Assembly-CSharp` field (Unity 2019.1+), never from `m_Target`'s guid.
- UI Toolkit (`.uxml`/`.uss`/`.tss`) uses **two** reference forms — see the matrix below; USS's `resource("…")` form is a documented exclusion.
- `.meta` files are both identity source *and* reference source (importer remaps).
- Special guids: all-zeros = null (skip); `…e000…`/`…f000…` = Unity built-ins (display as "(Unity builtin)"; excluded from unresolved/broken accounting).
- **Two distinct GUID encodings exist and must not be confused:** plain 32-hex (the real AssetDatabase guid) vs. dashed UUID (`b1fb53e3-1ca7-…`) — Shader Graph/VFX Graph's *internal* node/property identity, present dozens of times per file. Only the first is ever a cross-asset reference.

### GUID edge coverage matrix (must-catch forms, confirmed against real Unity output)

| Edge | Serialization form |
|---|---|
| Prefab/Scene/SO → script | `m_Script: {fileID: 11500000, guid: X, type: 3}` (plain YAML) |
| Prefab → nested prefab | `PrefabInstance` doc, `m_SourcePrefab: {fileID: 100100000, guid: X, type: 3}` |
| Renderer → material | `m_Materials:` array elements |
| Material → shader / texture | `m_Shader:` / `m_TexEnvs` → `m_Texture:` (plain YAML) |
| **ShaderGraph → texture/asset** | JSON-in-JSON with **backslash-escaped quotes**: `"{\"texture\":{…\"guid\":\"X\"…}}"` — a bare-quote regex silently misses every one of these |
| ShaderGraph internal IDs (must NOT index) | dashed UUID `"m_GuidSerialized": "b1fb53e3-…"` — never a cross-asset ref |
| asmdef → asmdef | `"references": ["GUID:0123…"]` |
| **asmdef → precompiled plugin assembly** | `"precompiledReferences": ["Vendor.dll"]` — a bare FILE NAME, no guid and no path anywhere; invisible to the guid regex, so it needs its own extraction and its own table (see `dll_refs` below) |
| UnityEvent → method | guid/fileID ref + adjacent `m_MethodName:` + `m_TargetAssemblyTypeName:` |
| UI Toolkit → asset (guid form) | `guid=X` URL query parameter |
| UI Toolkit → asset (path form) | `src="…"`, `url(…)`, `@import` — no guid at all |
| Any asset → registry package asset | Normal `guid:` form; target lives under `Library/PackageCache` |
| Addressables → asset | `AddressableAssetSettings.asset`'s `m_GroupAssets` (ordinary `{fileID,guid}` refs); each group asset's `m_SerializeEntries[].m_GUID` (**`m_GUID`, not `m_AssetGUID`** — confirmed against real output, see Liveness section) |

## GUID/asset graph indexer

### Project detection & config

- **No required path argument.** Walk up from cwd looking for `ProjectSettings/ProjectVersion.txt`, like `git` walks up for `.git/`. `-p/--path` overrides the walk's starting point.
- Optional `unbramble.json` at project root; sensible defaults: roots `["Assets", "Packages", "LocalPackages", "ProjectSettings"]`, excluded dirs (`Library`, `Temp`, `obj`, `Logs`, VCS dirs, `.unbramble`), DB at `.unbramble/unbramble.db` — a project-root state directory, not inside `Library/` (see "State directory: `.unbramble/`, not `Library/`" below for why).
- **`Library/PackageCache` is scanned identity-only** (parse `.meta` guids into the identity map; never parse package internals as reference sources). Without this, every URP/TextMeshPro/Input System reference reports as UNRESOLVED. Not watched in real time — refreshed on sweep/explicit reindex only.
- Startup assertion: `ProjectSettings/EditorSettings.asset` must show Force Text (`m_SerializationMode: 2`); fail fast with a clear error otherwise.

### Scanner

- Enumerate roots recursively, **following junctions/directory symlinks** (.NET skips reparse-point recursion by default — must be deliberate), deduping by canonical real path, tolerating dangling links with a warning (real projects have them; never crash). Root-level junctions are scanned with bounded parallelism (the main fan-out point on large real projects — several disjoint package trees hanging directly off `Assets/`); the ordinary root walks serially first so a directory reachable without a link keeps precedence, and output ordering stays deterministic regardless of scheduling.
- Store paths project-relative with forward slashes, *as seen through the junction*; persist the real-root → project-prefix mapping (the watcher, running in a different process later, must rewrite real-path events back to project paths using the *same* mapping, not a recomputed one).
- **Mirror Unity's own hidden-asset rules** (dot-prefixed names, `~`-suffixed folders like `Samples~`, folders named `cvs`, `.tmp` files) or the index diverges from what Unity itself sees — package `Samples~` folders ship real assets with real `.meta` files.
- Files of interest: every `*.meta` (identity); text-serialized asset extensions (`.prefab .unity .asset .mat .anim .controller … .shadergraph .vfx .uxml .uss .tss` etc.) as reference sources; `ProjectSettings/*.asset` as guid-less reference sources; `.cs` files for the C#/Roslyn side. Binary assets participate as targets via their `.meta` only.

### Parser

- Line-streaming (never load whole files — scenes can be hundreds of MB), single pass per file.
- One regex covers all guid serialization forms:
  ```
  (?i)(?:guid(?:\\"|")?:\s*(?:\\"|")?|guid=)([0-9a-f]{32})(?!\w)
  ```
  Handles plain YAML, plain JSON, escaped-JSON-in-JSON Shader Graph form, and the UI Toolkit `guid=` form in one pattern; excludes dashed UUIDs by construction (32 bare hex only). Normalize captures to lowercase. Under NativeAOT, use `[GeneratedRegex]`, never `RegexOptions.Compiled` (the latter silently degrades to interpreted matching).
- Lightweight streaming state (not a real YAML parser): track document boundaries (`--- !u!<ClassID> &<fileID>`, tolerating ` stripped`) so every ref is tagged with the component doing the referencing; capture GameObject names and component→GameObject links; a small bounded lookahead captures `m_MethodName`/`m_TargetAssemblyTypeName` next to UnityEvent target refs.
- UI Toolkit path-based refs get a second small extraction pass. **Store them unresolved and resolve at query time** against current file paths — never bake a resolved guid in at parse time (a baked guid goes stale exactly when the *target* moves, making the tool actively wrong — this design error was caught and redesigned during review; don't reintroduce it). Query-time resolution also makes broken path-refs fall out for free.
- Filtering: skip the null guid and self-references; **store unknown targets anyway** (they surface as unresolved/broken/external — dropping them hides real breakage).

### Store (SQLite, WAL mode)

- `files(id, path UNIQUE COLLATE NOCASE, guid NULLABLE, kind, mtime, size)` — one row per asset; guid nullable because source-only files (`ProjectSettings/*`) and not-yet-imported files are real graph nodes.
- `refs(source_file_id, target_guid, line, context, source_classid, method_name, target_type_name, property_path)` — GUID edges. `property_path` is best-effort display metadata from `YamlPropertyPathTracker`; it never affects resolution or confidence.
- `path_refs(source_file_id, target_path, line, context)` — UI Toolkit path-based edges, unresolved.
- `dll_refs(source_file_id, target_name_raw, target_name_norm, line, context)` — `.asmdef` plugin references keyed by filename. Resolve names at query time and merge them in C#, not `all_refs`, because suffix matching can't use an index. One exact file match is `proven`; ambiguity is `advisory`.
- `roots(real_path, project_prefix)` — persisted junction mapping.
- `gameobjects` / `component_gameobject` — display-name resolution tables.
- `name_hints(id, source_file_id, name, kind, line, type_name)` — negative-evidence names for liveness screening (see below); **not an edge store** — no targets, never walked/joined into any closure or `who-uses` result.
- `build_reachable_cache(file_id)` + `build_reachable_state(valid, config_key, graph_generation)` — derived reachability cache. Graph mutations invalidate it transactionally; publication verifies the expected generation before replacing rows. A miss may slow a query but can't change its answer.
- An `all_refs` view unions both asset edge tables, resolving a `target_file_id` and carrying a `kind` (`'guid'`/`'path'`) discriminator. A separate `cs_file_refs` view projects Roslyn `symbol_refs` down to file→file edges at query time; `unified_walk_edges` unions the two — see Graph unification below.

**The graph walk is keyed on `files.id`, not on guid — this is a correctness finding, not a style choice.** Guid-less files are full graph nodes: referencers, targets, *and* interior nodes of a transitive walk. A guid-keyed walk structurally can't represent them. The guid branch's join against `files` must be a LEFT JOIN so unresolved/external guid refs still surface in output. "Walkable" and "has a guid" are orthogonal facts about an edge.

Store rules: recursive walks use a closure CTE followed by a non-recursive `MIN(depth)` display query and a depth cap. Diff batches delete before inserting, distinguish moves from live GUID collisions, and run transactionally. Install `busy_timeout` before lock-taking pragmas; current-schema opens are read-only. Every finite mutation owns `index-writer.lock`, separate from the watcher's lifetime lock. NativeAOT requires `SQLitePCL.Batteries_V2.Init()`.

### Freshness: incremental refresh + background watcher

#### State directory: `.unbramble/`, not `Library/`

All owned state lives under project-root `.unbramble/`, never Unity's `Library/`. A detached watcher holds files open, and it must not block the standard workflow of deleting `Library/`. `init` excludes and configures VCS ignores for `.unbramble/`.

Two lock scopes now live in that directory: `watcher.lock` elects one long-lived watcher, while `index-writer.lock` serializes each finite SQLite mutation across watchers, explicit indexes, and inline query sweeps. Lock files are never stale state to delete; the OS-owned handles release on process death.

Governing invariant: **never wrong, only sometimes slower.**

- **Pull (always correct):** any CLI invocation stat-sweeps all in-scope files (metadata only), diffs against the DB (new → parse; changed → rebuild that file's rows across all derived tables; missing → cascade delete), then queries. Skipped when a fresh watcher heartbeat shows a live watcher already owns freshness.
- **Push (primary when running):** a detached watcher worker with one `FileSystemWatcher` per real root (they don't traverse reparse points), debounced ~1s, writing a heartbeat file per cycle. An asset and its `.meta` are always reparsed as one unit.
- **Trust heartbeats only when their schema stamp matches `CurrentSchemaVersion`.** A mismatch triggers an inline sweep and tells the user to retire the old watcher with `unbramble stop`.
- **Single active watcher, enforced:** exclusive `FileShare.None` lock file (`.unbramble/watcher.lock`); redundant workers exit successfully instead of piling up behind the owner (the OS releases the handle on process death — no stale-lock logic needed). On startup: start watchers buffering *first*, then catch-up sweep, then drain — sweeping before watching leaves an unwatched gap that the heartbeat then vouches for.
- **Self-heal:** a periodic full stat-sweep backstop (default ~5 min) regardless of events, plus an immediate resync on `FileSystemWatcher`'s `Error` (buffer-overflow) event. Directory renames/deletes are a known self-heal-only gap (one event fires, none for descendants).
- **Monitoring:** `unbramble monitor` ensures a detached worker exists, then polls its status file for live progress. Ctrl+C closes only the monitor; the worker continues until its idle timeout or `unbramble stop`. The monitor presentation never affects graph correctness.

### Self-verifying freshness

Every query calls `EnsureFresh`: trust a current compatible heartbeat or run an inline sweep before answering. Never substitute a session-start check, per-turn hook, or other ambient trust window.

Sweeps longer than about 1.5 seconds report progress and keepalives; fast sweeps stay quiet. Text mode keeps ordered diagnostics and results on stdout. JSON mode reserves stdout for JSON and sends diagnostics to stderr.

### Background watcher lifecycle

Self-verification (above) already guarantees every query is *correct*. The background worker closes the remaining speed gap without weakening that guarantee:

- **Trigger:** `unbramble monitor` always attempts an idempotent worker start before displaying status. Query verbs also start one after an inline sweep (subject to `watch.autoStart`) so the next query can use a fresh heartbeat. The actual `watch-worker` verb is a hidden process entry point, not user-facing CLI.
- **Launch with `UseShellExecute = true` and a hidden window.** `CreateProcess` handle inheritance can keep a caller's captured stdout/stderr pipes open after the query exits. The worker replaces its own writers with `TextWriter.Null`; status flows through `.unbramble/watch.status.json`.
- **Worker lifetime:** if `WatcherLock` is already held, a redundant worker exits 0 immediately. The owner self-terminates after an idle TTL (`AutoIdleGate`, default ~2 hours) of no fast-path query activity; `EnsureFresh` touches `.unbramble/watch.lastquery` whenever a query uses its heartbeat.
- **Crash-loop guard:** before spawning, the query path checks `.unbramble/auto-spawn.lastattempt` (`AutoSpawnPolicy`, default 3-minute cooldown) and skips spawning if the last attempt — regardless of its outcome — was too recent. This bounds retry frequency if a spawned process keeps failing; queries stay exactly as correct either way, just without the speed benefit until a spawn eventually succeeds.
- **Config toggle:** `unbramble.json`'s `watch.autoStart` (default `true`) turns this off entirely for a project. `unbramble init` mentions the default in its first-run output.
- **Every failure falls back to an inline sweep.** Spawn markers are telemetry only and never participate in `EnsureFresh`'s proof.

## C# semantic graph (Roslyn)

Roslyn (`Microsoft.CodeAnalysis.CSharp`, bundled at build time) does real semantic analysis: method calls, type/field references, inheritance, overload/generic resolution, MonoBehaviour lifecycle entry points.

- Compilations are built per asmdef/predefined-assembly unit. **Mode A** (semantic): a generated `.csproj` exists for the unit and parses to ≥1 define/reference — trust it, build a real Roslyn compilation. **Mode B** (syntactic): no usable csproj — parse-only, far blinder (only `inherit` and `call` ref kinds are ever emitted; no `type-ref`, no `member-access`). Any syntactic assembly disqualifies liveness claims (`dead-candidates`) entirely — see below.
- Mode A trusts a parseable generated csproj, but queries report a `csproj-stale` blind spot when its inputs are newer. `dead-candidates` rejects stale csprojs.
- Mode B persists a diagnostic reason: `no-csproj`, `csproj-unusable`, or `csproj-parse-failed`. Queries and `stats` name affected assemblies instead of hiding partial semantic coverage.
- Extraction coverage that liveness soundness depends on (must not regress): generic type arguments in invocation/object-creation expressions (`AddComponent<Foo>()`, `GetComponent<Foo>()`, `CreateInstance<Foo>()`); every identifier/qualified name in type position (casts, `is`/`as`, locals, generics), not just declaration sites; method-group/delegate-conversion references (`AddListener(Helper.Handle)`, `Action f = Helper.Handle`) as normal resolved `call`-kind refs; attribute names on types/methods; string literals passed to known by-name dispatch APIs (`SendMessage`, `BroadcastMessage`, `Invoke`, `InvokeRepeating`, `StartCoroutine`, `CancelInvoke`) captured into `name_hints` as negative evidence, never as edges.

## Graph unification: one seam, no third edge store

`who-uses`/`uses --transitive` cross between the asset graph and the C# graph through a **query-time SQL view**, not a persisted third edge table — both stores keep their own shape, refresh discipline, and deletion cascades:

```sql
-- symbol_refs projected to file->file edges; doc_id -> declaring-file resolution happens
-- HERE, at query time (the same "never bake a resolved id at parse time" lesson as path_refs).
CREATE VIEW cs_file_refs AS
SELECT sr.source_file_id, 'cs' AS kind, sr.target_doc_id AS target_key,
       tgt.file_id AS target_file_id, sr.line, sr.ref_kind, sr.confidence
FROM symbol_refs sr
JOIN symbols tgt ON tgt.doc_id = sr.target_doc_id
WHERE tgt.file_id IS NOT NULL AND tgt.file_id != sr.source_file_id;

-- The relation both who-uses' transitive walk AND dead-candidates' liveness propagation walk.
CREATE VIEW unified_walk_edges AS
SELECT source_file_id, target_file_id FROM all_refs WHERE target_file_id IS NOT NULL
UNION
SELECT DISTINCT source_file_id, target_file_id FROM cs_file_refs;
```

- **One hop = one file-level edge, regardless of kind.** `Player.prefab —guid→ Foo.cs` is depth 1; `Foo.cs —cs→ CoreUtil.cs` from there, it's depth 2. Symbol-level precision is *annotation* on a file edge, never extra depth — this keeps `--depth N` meaning one consistent thing.
- **Confidence is derived at query time:** `proven` for exact resolution, `advisory` for ambiguity or file-level projection, and `speculative` for name-only leads from syntactic assemblies. Ambiguity only downgrades confidence. Answer confidence is the weakest path edge; speculative leads don't lower a stronger answer beside them.
- **Relevant answers carry `blindSpots`:** `string-path-loading`, `reflection`, `syntactic-assemblies-present`, `addressables-unconfirmed`, `depth-truncated`, and `csproj-stale`. Syntactic coverage includes named assembly reasons and remediation. Symbol queries with no strong result set `possibleFalseNegative`. Asset-only answers suppress unrelated C# caveats unless `--verbose`.
- Unresolved refs and unresolved doc_ids stay in their own unresolved bucket, never given a confidence label — confidence is never a place to hide breakage.

## Liveness / `dead-candidates`: forward reachability, file-granular

Forward reachability from a defined root set — **never** "nothing references this" (a much weaker claim). Roots are file nodes, materialized at query time from path rules and existing edges (no new tables):

- Every non-identity-only `ProjectSettings/` file, including disabled Build Settings scenes.
- Every file under a `Resources/` segment or `Assets/StreamingAssets/`.
- Addressables entries, but only when the resolved package version is confirmed; otherwise liveness is unavailable.
- Semantic C# entry points: `[RuntimeInitializeOnLoadMethod]`, `[InitializeOnLoadMethod]`, and `static Main`. MonoBehaviour lifecycle methods become live through ordinary file reachability, not as unconditional roots.

UnityEvent and asmdef→plugin edges live outside `unified_walk_edges`; one shared `SeedExtraWalkEdges` helper seeds them into both reachability computations.

Reachability is file-granular over the same edge relation `who-uses` walks. Every resolved outgoing edge from a live file makes its target live. Therefore a `provenDead` file can't also be the target of a resolved edge from a live file.

**Referenced-by-convention files are excluded from the candidate universe outright** (never proven, never advisory): `*.asmdef`, `*.asmref`, `link.xml`, `csc.rsp`, and any embedded package's `package.json` — Unity consumes these by convention, not by reference, so the graph structurally can't reach them, but deleting one silently restructures compilation.

### Build-reachable tag on `who-uses` (screen-free, ungated reuse of this walk)

`who-uses` and missing-reference results tag whether each source is proven build-reachable and support `--build-reachable-only`. The screen-free walk reuses the same roots and propagation but makes only a positive claim: false means "not proven build-reachable," never "unreachable." Screens don't seed this walk, while detected Addressables entries do even when the version is unconfirmed.

### Screens: seed liveness, don't just suppress

A candidate that fails to clear every screen is **seeded into the live set** (not merely hidden from output) and reported as `advisoryDead` with its reason — seeding matters because a maybe-live file's own dependency chain must survive with it, exactly like the user-supplied allowlist (`unbramble.json`'s `liveness.allowlist`, which uses the identical seed semantics).

| Screen | Rule |
|---|---|
| Path-ref name collision | an unresolved *path-kind* ref whose final path segment matches the candidate's filename |
| Syntactic-text collision | an unresolved `symbol_refs` row whose trailing identifier matches a symbol the candidate declares |
| Name-hint collision | a `name_hints` row from a **live** file (SendMessage/Invoke literals, animation-event names, guid-less UnityEvent bindings) matching a candidate method name |
| Attribute screen | the candidate declares a type/method with any attribute outside a curated inert list (`[Serializable]`, `[SerializeField]`, `[Header]`, …) — catches reflection-driven frameworks (custom editors, DI, serializers, test runners) |
| Disabled-region screen | an identifier inside a `#if`-disabled region of a **live** file matches the candidate's declared names — catches wrong-scripting-defines blind spots |
| Interface/virtual-dispatch guard | the candidate implements a **live** interface or overrides a **live** base member — polymorphic calls resolve to the base doc_id, not the implementation's, so it can have zero direct inbound refs while live |
| No-extracted-symbols screen | a `.cs` candidate with zero rows in `symbols` (e.g. a whole-file platform-gated file with no declared symbols the other screens can match against) is treated as advisory by default — closes a confirmed false-`provenDead` gap found on a real project (a whole-file `#if SOME_SDK_ANDROID`-style plugin-define-gated class referenced from a live file's disabled region, with nothing to match it to) |
| Unity-callback guard | a type invoked only through a metadata-only interface (e.g. `ISerializationCallbackReceiver`) or a known Unity callback base class (`AssetPostprocessor` et al.) has no ordinary inbound call edge — screened via a `cs-unity-callback` name hint so it isn't silently proven dead |

### UnityEvent method linking

Type selection for a bound method comes **only** from `m_TargetAssemblyTypeName`  — never from `m_Target`'s guid, which identifies the containing asset, not a script. Cascade: exact single member match in a semantic-mode assembly → `proven`; multiple overloads → `advisory` link to every overload (never guess which one Unity picked); no declared match → walk the project's `inherit` chain upward, any match found this way is `advisory`; no type resolvable or no match anywhere → an unmatched-advisory edge carrying the raw name, and the name enters the liveness screen set. Guid-less same-asset bindings (the common case — button and handler in one prefab) have no resolvable target chain at all; they contribute a `name_hints` row only, and rely on the handler's own `m_Script` attachment for file-level liveness.

### Output contract

`dead-candidates` preflight gates (any failure ⇒ `liveness unavailable: <reason>`, exit 1, **zero candidates emitted** — never a partial/degraded list): Force Text; freshness proven; all assemblies semantic (one syntactic assembly disqualifies the whole run); no Addressables, or Addressables confirmed for the detected version; generated csprojs not stale relative to `Packages/manifest.json`, `ProjectSettings/ProjectVersion.txt`, `ProjectSettings/ProjectSettings.asset`, `ProjectSettings/EditorUserBuildSettings.asset`. All failed gates are listed at once, never just the first.

Scope is **file-level only** — not individual dead methods. Member-level proofs would have to survive by-name dispatch (SendMessage strings, animation events, UnityEvents, reflection), and after every honest screen the proven bucket would be nearly empty; file-level is the flagship "most of this project is removable" case and a much stronger claim. Deferred, not abandoned.

The blind-spots footer is **unconditional output**, every run, both formats — not a flag. The tool's job stops at "provably unreachable by static analysis, blind spots stated" — it explicitly prescribes, and its own output text points at, the workflow that absorbs residual risk: propose a small batch → delete → run the project's own smoke tests → merge if green → repeat.

## Windows Defender exclusions

`unbramble defender setup` elevates PowerShell once through UAC to call `Add-MpPreference`. These constraints are load-bearing:

- Pin every launched process to `Environment.SystemDirectory`; never inherit the caller's project directory.
- Elevate `powershell.exe` directly with `runas`; don't elevate `unbramble.exe` and add a second process hop.
- Write the command to a short-lived UTF-8-with-BOM `.ps1` file and invoke it with `-File`; `ShellExecuteEx` parameters are too short for a real multi-entry encoded command.
- Exchange data through `.unbramble/defender-plan.json` and `defender-result.json`; redirected stdout isn't available across `UseShellExecute = true` elevation.

## Known gaps and deliberately deferred work

- **Addressables**: confirmed for **1.21.x, 2.3.x, and 2.8.x** (`AddressablesDetector.ConfirmedRanges`). Widen ranges only after capturing real serialized output; unknown versions remain unconfirmed.
- **Member-level dead-code detection** — deferred; file-level is the shipped scope.
- **Multi-build-target define union** — deferred; disabled-region screening protects file-level liveness meanwhile.
- **Mode C** reconstruction from `Library/ScriptAssemblies` — deferred; syntactic assemblies already disable liveness.
- **MCP wrapper** — deferred. Any future wrapper must stay thin, call the same query layer and `EnsureFresh`, and preserve the freshness report.
- **Indexing performance** — no-change sweeps meet the target. Roslyn scheduling still lacks a width/memory cap at very high assembly counts; persisted compilation artifacts and USN-journal sweeps aren't yet justified.
- **`link.xml`-preserved code paths, native plugin callbacks, `Type.GetType(string)`/DI-by-name reflection** — permanent, disclosed blind spots, absorbed by the delete-batch → smoke-test workflow, not by tool confidence.

### Evaluated, not yet built

- **`inspect <asset> [--component TypeName]`** — bounded raw serialized-field output first; prefab override resolution only after it can avoid confidently wrong merges; enum labels require richer symbol schema.
- **Resolved `uses` grouping** — collapse repeated prefab modification rows in text output and add a target-file-kind filter without changing JSON.
- **C# `who-uses` grouping** — group referencers by file in text output, with `--verbose` expansion.

### Re-raised and decided

- **C# caveats on asset-only queries:** omit irrelevant C# warnings unless `--verbose`; freshness still runs normally.
- **Historical GUID tombstones:** declined because persisted history can outlive its accuracy. `resolve` reports only current-index truth.

## CLI shape

```
unbramble init [path] [--no-agents]  first run: build the index, write AGENTS.md/CLAUDE.md, print watcher setup guidance
unbramble index [path] [--full]    explicit refresh / rebuild
unbramble monitor [path]           ensure the background watcher exists and show live progress
unbramble stop                     stop live unbramble background processes
unbramble uninstall [path] [-y|--yes]       remove UnBramble's integration and generated state from one project
unbramble uninstall --machine [-y|--yes]    remove the manual ZIP installation from this machine
unbramble defender status|setup|remove [path]   Windows Defender exclusion setup (see README)
unbramble who-uses <path|guid|symbol> [--transitive] [--depth N] [--kind guid|path|cs|event|dll] [--under prefix]
unbramble who-uses --guids <file> [--json|--jsonl]
unbramble uses <path|guid> [--transitive] [--missing-only] [--summary|--group-by-target] [--top N] [--build-reachable-only] [--fail-if-found] [--under prefix]
unbramble audit-assets <paths-file> --missing [--group-by-target] [--top N] [--build-reachable-only] [--json|--jsonl]
unbramble cs-refs <symbol|doc-id>
unbramble resolve <path|guid|name-fragment>
unbramble stats [path] [--unresolved] [--collisions]
unbramble dead-candidates [path] [--json] [--include-advisory] [--kind assets|cs|all]
```

Every public verb supports `--help` and `-p/--path` where a project is relevant. Data-producing verbs support `--json` for machine consumption. Query paths normalize `\` to `/` at the engine boundary, so paths copied from Windows tools work identically to Unity-style paths.

**Missing-reference audit workflow:** `uses <asset> --missing-only` returns owner GameObject, component/script, serialized property path, prefab override/source context, `m_Script` classification, and source build reachability for each unresolved link. `--summary` / `--group-by-target` collapses repeated target keys while retaining distinct owner fields/components/objects/source prefabs and counts; `--top N` limits groups; `--build-reachable-only` filters noisy dead/sample sources. `audit-assets <paths-file> --missing` runs the same query for many assets after one freshness snapshot and one reachability load; `uses --missing-only --paths <file>` is an exact alias. `who-uses --guids <file>` does the same for reverse GUID lookups. Batch commands report per-target progress on stderr and support JSONL so automation receives one complete object per target without corrupting stdout JSON.

**High-fan-out scoping:** `--under <prefix>` scopes either query verb to one location (referencers for `who-uses`, dependencies for `uses`; rows without a path on the filtered side are excluded — a location filter only keeps what provably has that location). Independently, `uses`' TEXT rendering collapses `Library/PackageCache/` dependencies to one counted line once they outnumber a small threshold — never silently (the line states the count and both expansion routes), never in `--json`, and never under `--verbose` or an explicit `--under` (scoping IS the expansion request).

**Text output has two renderings, and which one you get is decided by `ConsoleCapabilities.TerminalWidth` alone** (non-null only when stdout isn't redirected, or when `UNBRAMBLE_COLUMNS` forces a width — that override exists because capturing the output is precisely what suppresses the human rendering, so it's the only way to see or test it). A human terminal gets prose wrapped to the width with a hanging indent (`Program.WriteParagraph`, via the ANSI-aware `TextWrap`), blank-line grouping between findings, one blank line above and below the whole invocation (`Main`'s spacer wrapper, so an answer doesn't collide with the shell prompt), and `stats`' key/values padded into an aligned column. A redirected stream gets exactly one unwrapped line per statement, no spacers, no padding — the shape agents and `grep` want, and the shape the CLI text tests assert against.

- **The invariant: layout is human-only, and the two renderings must never diverge in *words*.** Wrapping/padding/spacers are the only permitted difference; anything that changes what is said belongs in both. Making the wrap unconditional would silently break every text assertion in the suite and every agent parsing text output, which is why the gate is a single property rather than a per-call-site decision.
- Layout is deliberately gated separately from color (`SupportsAnsi`): `NO_COLOR` in a real terminal still wants wrapped, aligned prose, just uncolorized. Width is read live (not cached like the color capabilities) because a terminal can be resized under a long-lived `watch`.
- `TextWrap` measures *visible* width — escape codes occupy no columns — and only ever breaks at a space between words, so a line can never end mid-escape-sequence and print the tail as literal garbage.

### Agent integration and project footprint

UnBramble is intentionally an ordinary local CLI with a small, explicit footprint:

- **No agent runtime integration.** UnBramble makes no AI/model calls, creates no embeddings, exposes no MCP server, installs no agent hooks, and performs no runtime or per-turn prompt/context injection. The background watcher only maintains the local filesystem index.
- **Static discovery only.** A fresh coding-agent session otherwise has no way to learn UnBramble exists, so `init` upserts an idempotent, version-stamped, marker-delimited block (`<!-- unbramble:begin/end -->`) into project-root `AGENTS.md`.
- **User prompt surfaces remain user-owned.** If `CLAUDE.md` doesn't exist, `init` creates a small shim pointing at `AGENTS.md`. An existing file that already references `AGENTS.md` is left alone; an existing file with other content and no reference is never edited. A pre-existing hand-written `## unbramble` section in `AGENTS.md` is migrated into the managed block once; all other content is preserved.
- **Setup is deliberate, announced, and reversible.** Instruction and VCS-ignore edits happen only during `init`, never during a query or watcher pass. Setup records a small rollback receipt under `.unbramble/`: unchanged files return to their prior bytes during uninstall; if the user edited around UnBramble's content, only recognizable owned content is removed. `init --no-agents` skips agent-file setup but doesn't remove an existing managed block.
- **Generated state is contained.** Index, watcher, and Defender bookkeeping lives under project-root `.unbramble/`; `init` adds an appropriate VCS-ignore entry when Git or Plastic SCM is detected. Indexing and queries never modify Unity assets or source files. Defender exclusions are offered only with explicit consent and are removable with `unbramble defender remove`.
- **Removal is bounded and explicit.** `unbramble uninstall [path]` first lists its exact effects and asks for confirmation, then stops all live UnBramble processes, removes only Defender exclusions recorded as added by UnBramble, restores or surgically cleans project setup files, and deletes `.unbramble/`. After projects are clean, `unbramble uninstall --machine` separately confirms the exact user-`Path` and install-directory removal, stops all live UnBramble processes, updates `Path`, and launches a one-shot helper to delete the tightly validated manual-install directory after the running executable exits. Both accept `-y` or `--yes` for deliberate non-interactive use. See [installing](installing.md).

**Exit codes**: `0` = the command/query executed successfully, including when a missing-reference query found broken links; `1` = environment/usage error (not a Unity project, Force Text off, bad args, or `dead-candidates`' liveness-unavailable gate); `2` = query target not found/ambiguous; `3` = findings were present and the caller explicitly requested the CI gate with `--fail-if-found`. Findings are data, not tool failure, unless the caller opts into that policy.

**`resolve` on a well-formed guid that matches nothing is exit 0, not exit 2.** `who-uses`/`uses` already answered an unmatched bare guid gracefully — `ResolveQueryTarget` treats it as a valid target with a null `FileId`, since direct refs by literal guid are still answerable — while `resolve` reported "no match" and exited 2 on the same input. An agent probing an unknown guid's identity reaches for `resolve` *first*, so it hit the error path on a perfectly good question. A 32-hex guid absent from the index is an *answer* ("not in this index — a deleted asset, or one from a package that isn't installed", JSON `unresolvedGuid: true`); a non-guid query that matches nothing is still a failed lookup and still exits 2.

**`cs-refs` reports UnityEvent bindings and carries the standard caveat footer.** It previously read `symbol_refs` alone, so a method whose only caller was a serialized `Button.onClick` binding came back "0 referencers" — with no blind-spots footer either, making it the one unqualified zero the tool could print, and exactly the answer an agent reads as "safe to delete". `who-uses <symbol>` had those bindings all along (`GetEventLinksTargetingDocId` + `GetLocalEventBindingsTargetingDocId`); the two symbol-level surfaces had simply drifted. Both now read one shared `UnBrambleEngine.SymbolEventReferencers` accessor so they can't drift again. `cs-refs` deliberately stays the *narrower* verb — no declaring-file asset context, no speculative name-match fallback — and its empty answer points at `who-uses`, which asks the wider question, rather than quietly widening itself. Bindings are counted in their own section, never folded into the `symbol_refs` count, so that number keeps meaning what it always meant.

**Cross-cutting rules that apply everywhere in this codebase:**

- NativeAOT compatibility: `[GeneratedRegex]` (never `RegexOptions.Compiled`); explicit `SQLitePCL.Batteries_V2.Init()` at startup; avoid reflection-based serialization for `--json` output (hand-written or source-generated `System.Text.Json`).
- Paths: project-relative, forward slashes; all comparisons case-insensitive; tolerate spaces, brackets, non-ASCII.
- Streaming: never load whole asset files into memory. Line-stream, single pass per file.
- SQLite hygiene: WAL mode, `PRAGMA busy_timeout` on every connection, batch writes in transactions, deletions before insertions within a diff batch.
- Fail loudly, degrade honestly: unknown/unresolved targets are stored and surfaced, never dropped; ambiguity is reported as "can't tell", never guessed away.
- Never assume the target project's VCS is git (Plastic SCM/Perforce are common in Unity shops).
- Performance targets (design scale ~100k files): full index < 60s, no-change refresh < 2s, queries < 100ms. Targets, not license to trade away correctness — see the "Indexing performance" bullet in Known gaps above for what's still open.

## Glossary

- **guid** — Unity's 32-lowercase-hex asset identity, from the sibling `.meta` file. Distinct from **dashed UUIDs** (`b1fb53e3-1ca7-…`), which are Shader Graph/VFX internal IDs and never cross-asset references.
- **fileID** — identity of a document *within* one Unity YAML file (`--- !u!114 &8395…`); can be negative; can carry a trailing ` stripped` token.
- **identity-only file** — a file (e.g. under `Library/PackageCache`) whose `.meta` guid is indexed so refs to it resolve, but whose contents are never parsed as a reference source.
- **guid-less node** — a real graph node with no guid (e.g. `ProjectSettings/*.asset`, a hand-authored `.uss` with no `.meta` yet). First-class in the walk.
- **builtin guids** — `0000000000000000e000000000000000` / `0000000000000000f000000000000000` families: Unity built-in resources. Display as "(Unity builtin)"; excluded from unresolved/broken accounting.
- **proven / advisory / speculative** — the confidence tiers an edge or answer carries; see Graph unification above.
- **provenDead / advisoryDead** — `dead-candidates`' two output buckets; only `provenDead` is the actual "safe to consider deleting" claim, and even that's gated on the blind-spots footer and the delete-batch → smoke-test workflow.

## Where to look for more

- `docs/validation-runbook.md` — how to validate a fresh build/index against a real project, and how to run `dead-candidates`' delete-batch → smoke-test workflow safely.
- Everything else: git history.
