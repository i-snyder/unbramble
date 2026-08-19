# UnBramble Validation Runbook

Operational steps for validating UnBramble against a real Unity project, plus the three per-project adoption gates. A cold reader with a real Unity project and this repo checked out should be able to execute all of it without asking questions.

Mechanical ground truth (rg-parity, the Unity spot-check) proves the index is *correct*. Consumption/freshness/selectivity (section 3 below) prove it's *useful enough to recommend*. Both matter; neither substitutes for the other.

Dated results from past validation runs (real-project run logs, A/B protocol execution results) have been pruned from the current tree. What follows is the current, load-bearing procedure only.

## 0. Prerequisites

- A built `unbramble.exe` (`dotnet publish src/UnBramble.Cli/UnBramble.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true -o publish`, or let NativeAOT publish succeed if your machine has the VS C++ toolchain).
- `rg` (ripgrep) on PATH (`choco install ripgrep` on Windows).
- PowerShell 7 (`pwsh`) on PATH.
- A real Unity project using **Force Text** serialization (`Edit > Project Settings > Editor > Asset Serialization > Mode: Force Text`) -- `unbramble init` fails fast with a clear error otherwise; that error is correct behavior, not a bug.
- **Run `rg-parity.ps1` from a real, attached console (an interactive `pwsh`/terminal window), not a `nohup`-detached or otherwise console-less background launch.** A detached/console-less launch has produced spurious crash signatures under sustained rapid child-process spawning that are artifacts of missing console attachment, not real bugs in `unbramble.exe` or the script -- the identical command passes cleanly from a real console. If you must run it unattended, verify this doesn't apply to your launch method before trusting a crash as a real finding.

## 1. Real-project validation

### 1.1 Index the project

```powershell
<path-to>\unbramble.exe init <path-to-unity-project>
```

Timing expectations (design scale ~100k files -- not a hard gate, but worth noticing if wildly exceeded): full index under 60s. A first cold run on a very large project can still take minutes -- that's now expected/normal, not a known bug. On Windows, cold-cache Roslyn work is one contributor, but on a real production project the dominant cost turned out to be Windows Defender real-time scanning of a single external junction target, not Roslyn -- check `unbramble defender status` (init offers the exclusion setup; see `DefenderExclusionSetup`) before assuming a slow first run is a Roslyn/code issue. Note the timing either way when you record results, and check `.unbramble/index-history.log`'s per-root timings if a run is unexpectedly slow.

Sanity-check the index before trusting anything downstream:

```powershell
<path-to>\unbramble.exe stats <path-to-unity-project>
```

Confirm the file/edge counts look plausible for the project's size (roughly: assets in the thousands to tens of thousands for a mid-size project; a suspiciously low count usually means a root wasn't found, or Force Text isn't actually on for some assets even though the project-wide check passed).

### 1.2 Run rg-parity

```powershell
scripts\rg-parity.ps1 -ProjectRoot <path-to-unity-project> -Sample 200
```

This is the mechanical acceptance gate (guid-kind edges only): for a sample of guids, `unbramble who-uses <guid>` must exactly match a properly-scoped `rg` search for that guid's text. Read the per-guid `FAIL` lines if any appear -- each prints both sides (rg's file set vs. UnBramble's), which is usually enough to tell whether the bug is in scoping (the script) or parsing (the binary). Increase `-Sample` (or pass explicit `-Guids`) to widen coverage; the default 200 is a real-project sampling default, not a hard cap.

**On any mismatch: file an issue with the guid, the referencing file(s), and both result sets (rg's and UnBramble's) exactly as printed.** Do not "fix" a mismatch by tweaking the sample or squinting past it -- a real mismatch here is correctness gold: it is either a real parser bug (fix the regex/parser) or a real scoping gap in `rg-parity.ps1` itself (fix the script, and add the case to the fixture as a permanent regression test). Re-run after any fix to confirm the specific guid now passes, then re-run the full sample.

### 1.3 Run the Unity-native spot-check

Path-based refs (`.uxml`/`.uss`/`.tss`) have no guid text on disk, so rg-parity structurally cannot verify them -- Unity's own `AssetDatabase` is their only ground truth.

1. Copy `scripts/unity/ExportDependencies.cs` into `<path-to-unity-project>/Assets/Editor/ExportDependencies.cs`.
2. Edit its `AssetPaths` list to point at ~5 real assets in the project, including **at least one `.uxml` or `.uss`** and **one `.shadergraph`** (see the header comment in that file for why both matter).
3. In the Unity Editor: `Tools > UnBramble > Export Dependencies`. This writes `<path-to-unity-project>/unbramble-unity-deps.json`.
4. Run the comparison:

   ```powershell
   scripts\compare-unity-deps.ps1 -ProjectRoot <path-to-unity-project>
   ```

The comparison is **containment, not equality**: every dependency Unity's own `AssetDatabase.GetDependencies` reports must appear in UnBramble's `uses` output; UnBramble may legitimately report *extra* unresolved/external/builtin entries Unity's API omits (that's not a violation -- it's UnBramble surfacing broken/external refs Unity doesn't editorially care about). A `FAIL` here means UnBramble is *missing* a real dependency Unity found -- always a real bug (in the parser, the extension list, or the extraction regex for that reference form). File it the same way as an rg-parity mismatch: the asset path, the missing dependency, and the full `unbramble uses --json` output for that asset.

### 1.4 What "done" looks like for section 1

- `rg-parity.ps1` reports 0 mismatches (or every mismatch has a filed, understood, fixed-or-explicitly-deferred issue).
- `compare-unity-deps.ps1` reports 0 containment violations across all exported assets, including the `.uxml`/`.uss` and `.shadergraph` entries.

### 1.5 Addressables — known landmine, confirmed only for specific version ranges

Addressables serialization has been captured and verified against real projects three times. Facts worth knowing before touching this code:

- `AddressableAssetSettings.asset`'s `m_GroupAssets` and each group's `m_SerializeEntries[].m_GUID` (**`m_GUID`, not `m_AssetGUID`** — the original design doc's speculation was wrong) are matched by the existing generic guid regex with zero parser changes. The group's own top-level `m_GUID:` field is a different, Addressables-internal identity (not a real asset guid) and is excluded by a targeted regex exclusion (`RegexPatterns.AddressablesGroupSelfGuidField`).
- **Confirmed ranges** (`AddressablesDetector.ConfirmedRanges`): **1.21.x** (Unity 2022.3.x, full real-project capture) and **2.3.x** (Unity 6000.0.x, full real-project capture) are both independently capture-confirmed; **2.8.x** (Unity 6000.0.x) rides along on a byte-level public-source diff against the 2.3.x capture, not an independent capture of its own — one rung below the other two on the confirmation ladder, per the class doc. A project on any other resolved Addressables version fails `dead-candidates`' liveness gate with a message naming the actual version — that is the tool refusing to guess, not a bug to work around. **Before widening this range**, capture real output from the new version and verify field names against it — do not bump the version constants on changelog reading alone.

### 1.6 `dead-candidates` on a real project

`unbramble dead-candidates` is the flagship capability: forward reachability from a defined root set, never "nothing references this" — with the asymmetric-risk invariant meaning a false "provably dead" is unacceptable and a false "can't tell" only costs a deeper look. This section is the procedure for running it against a real Unity project.

#### 1.6.1 Preconditions

- The project already passes section 1's rg-parity / Unity-spot-check gates (an index that isn't provably correct on the mechanical layer isn't a sound base for a liveness claim).
- Every C# assembly in the project must analyze in **semantic mode** (`unbramble stats` reports `mode: semantic`, not `syntactic`, and not the mixed form). If any assembly is syntactic, open the project once in Unity/your IDE so it generates the per-assembly `.csproj` files Mode A needs (`Edit > Preferences > External Tools`, or just open a `.cs` file in Rider/VS — either regenerates them), then re-run `unbramble index`.
- If the project uses Addressables, its resolved package version must fall inside `AddressablesDetector.ConfirmedVersionRangeLabel` (currently **Addressables 1.21.x, 2.3.x, 2.8.x** — see 1.5 above for what backs each range). A project on a different Addressables version will see the liveness gate fail with a message naming the actual version; that is not a bug to work around.

#### 1.6.2 Run it

```powershell
<path-to>\unbramble.exe dead-candidates <path-to-unity-project>
```

Read the **entire** output before doing anything else:

- **`liveness unavailable: ...`** (exit 1) — every failed gate is listed, not just the first. Fix each one (semantic mode, Addressables version, or a stale generated csproj — the message tells you which) and re-run. Zero candidates are ever produced in this state; there is no partial/degraded answer to act on.
- **The root-set summary line** — sanity-check it against what you know about the project before trusting anything below it. A suspiciously low `ProjectSettings files`/`Resources/ files` count, or `Addressables: not detected` for a project you know uses Addressables (check `Packages/manifest.json` and `Assets/AddressableAssetsData/` by hand if unsure), means investigate the root cause before proceeding — the whole rest of the answer is only as good as this root set.
- **`provably unreachable (N files)`** — the actual candidate list, one path per line.
- **The blind-spots footer** — read it every time, not just the first time. It states, in plain terms, what static analysis cannot see: `Resources.Load("Foo" + var)`-style string-built paths outside `Resources/` (there are none, by construction), reflection (`Type.GetType(string)`, DI containers configured by name), asset-bundle build scripts, native plugin callbacks, and `link.xml`-preserved code paths.
- Pass `--include-advisory` to also see the **screened** set (files the tool concedes *might* be live — an attribute, a disabled `#if` region, a broken-but-suggestive path/name — and is therefore treating as live rather than proposing for deletion). Do not delete anything from this list on the strength of this tool alone; it is explicitly the "can't tell" bucket.

#### 1.6.3 The delete-batch → smoke-test → merge workflow

This is the workflow `docs/architecture.md`'s Liveness section prescribes, and the one every piece of `dead-candidates` output text points back to. The tool's job stops at "provably unreachable by static analysis, stated blind spots aside" — it is explicitly not a substitute for this workflow, only what makes the workflow safe to run at scale instead of by hand:

1. **Propose a batch.** Start small on the first real run (10-20 files), not the whole list — you're also validating the *tool* against this specific project's shape the first time. Prefer `--kind assets` and `--kind cs` as separate batches over one mixed batch, since the failure mode if something goes wrong differs (a missing asset breaks differently than a missing script) and it keeps the smoke-test diff easier to read.
2. **Delete the batch.** Actually remove the files (and their `.meta` companions for assets) — don't just comment out or rename; the whole point is proving the project still works without them.
3. **Run the project's own smoke tests.** Whatever that means for this project: play-mode entry, an existing automated test suite, a build. This step is not optional and not replaceable by re-running `unbramble` — `dead-candidates` proves "nothing in the *graph* reaches this", not "nothing at runtime needs this" (that's exactly the blind-spot gap the tool states on every answer).
4. **Merge if green.** If the smoke test fails, that is real signal: either a blind spot bit you (file it — see 1.6.4) or the smoke test itself was inadequate (also worth knowing).
5. **Repeat** with the next batch. Re-run `unbramble dead-candidates` fresh each round — deleting the previous batch can make previously-screened or previously-live files newly provable (a screened file's only "maybe-live" reason might have been another file you just deleted).

#### 1.6.4 What "done" looks like for section 1.6, and what to file on a surprise

- At least one full batch cycle (propose → delete → smoke-test → merge) completed on a real project, with the outcome recorded (files removed, smoke-test result).
- Any case where the smoke test failed on a `provenDead` (not merely advisory) file: this is the single most important thing this tool must never get wrong. File it with the exact file path, the full `dead-candidates --json` output for that run, and what the smoke test showed — treat it as a correctness bug in the tool (a missed reference form, a gate that should have fired and didn't) until proven otherwise, never dismiss it as "must have been something else."
- Any gate that fired when you didn't expect it (e.g. `csproj-stale` immediately after an IDE reopen) — usually correct behavior, but worth a note if the timing was surprising, since it's a signal for whether the mtime-based check is well-calibrated in practice.

## 2. The three per-project adoption gates

This checklist is intentionally blank: apply it separately to each consuming project before making UnBramble part of that project's default agent workflow. Record local evidence inline (a link, log excerpt, or date) rather than just checking a box. The repository's fixture suite proves the mechanisms; it does not prove usefulness or watcher behavior against every project's layout.

- [ ] **Consumption** -- agents actually call `unbramble` during real work without prompting beyond the checked-in project instructions.
- [ ] **Freshness** -- the index updates without agent labor. Verify the background watcher path (`unbramble monitor` starts it), not only the pull/stat-sweep fallback; staleness must be bounded and never silent.
- [ ] **Selectivity** -- one query returns the small relevant edge set instead of forcing the agent into greps and file reads to reconstruct the same picture.

All three checked, with project-specific evidence, is the adoption bar. Fewer than three is not "mostly there" -- keep usage explicit until the missing gate is demonstrated.
