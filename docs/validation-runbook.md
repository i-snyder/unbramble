# UnBramble Validation Runbook

Use this runbook to validate a release candidate against a real Unity project. The fixture suite proves the mechanisms; this proves they work against the shape of a consuming project.

## Prerequisites

- `publish/unbramble.exe` from `./scripts/verify-all.ps1`
- PowerShell 7 and `rg` on `Path`
- A real Unity project using **Force Text** serialization
- An attached terminal for `rg-parity.ps1`; don't trust crash signatures from a console-less background launch without reproducing them interactively

Don't publish proprietary project content when recording or reporting results. Follow [CONTRIBUTING.md](../CONTRIBUTING.md).

## Validate the graph

### 1. Index the project

```powershell
<path-to>\unbramble.exe init <path-to-unity-project>
<path-to>\unbramble.exe stats <path-to-unity-project>
```

Confirm file and edge counts look plausible and every C# assembly reports semantic mode. If a cold index is unexpectedly slow, check `unbramble defender status` and `.unbramble/index-history.log` before assuming Roslyn is responsible.

### 2. Compare GUID references with ripgrep

```powershell
scripts\rg-parity.ps1 -ProjectRoot <path-to-unity-project> -Sample 200
```

The script compares `unbramble who-uses <guid>` with a correctly scoped text search. The pass condition is zero mismatches. A mismatch is either a parser bug or a parity-script scoping bug; inspect the two printed file sets and add a synthetic regression fixture before fixing it.

### 3. Compare path references with Unity

Path-based UI Toolkit references have no GUID text for ripgrep to verify, so compare them with Unity's dependency API:

1. Copy `scripts/unity/ExportDependencies.cs` into the project's `Assets/Editor/` folder.
2. Set its `AssetPaths` list to about five real assets, including a `.uxml` or `.uss` file and a `.shadergraph`.
3. In Unity, run **Tools → UnBramble → Export Dependencies**.
4. Run:

   ```powershell
   scripts\compare-unity-deps.ps1 -ProjectRoot <path-to-unity-project>
   ```

The pass condition is zero containment violations: every dependency Unity reports must appear in `unbramble uses`. UnBramble may also report unresolved, external, or built-in references Unity omits.

## Validate `dead-candidates`

Before running liveness analysis, the project must pass the graph checks above, every C# assembly must be semantic, and any installed Addressables version must fall within `AddressablesDetector.ConfirmedRanges`. Don't bypass a failed gate.

```powershell
<path-to>\unbramble.exe dead-candidates <path-to-unity-project>
```

Check three parts of the result:

- `liveness unavailable` lists gates that must be fixed before any candidate can be trusted.
- The root summary should match the project's Build Settings, `ProjectSettings`, `Resources`, `StreamingAssets`, and Addressables use.
- The blind-spots footer names runtime conventions static analysis can't prove.

`--include-advisory` shows files UnBramble deliberately treats as maybe-live. Don't delete them based on this tool alone.

For proven candidates:

1. Start with a batch of 10–20 files, separating assets and C# when practical.
2. Delete the files and their `.meta` companions.
3. Run the project's own play-mode, test, or build smoke checks.
4. Merge only if those checks pass.
5. Re-run `dead-candidates` before selecting another batch.

A smoke-test failure after deleting a proven candidate is a correctness bug until shown otherwise. Preserve the full JSON result and the smallest redacted reproduction.

## Release gate

- [ ] `rg-parity.ps1` reports zero mismatches.
- [ ] `compare-unity-deps.ps1` reports zero containment violations, including UI Toolkit and Shader Graph assets.
- [ ] `dead-candidates` passes every availability gate.
- [ ] At least one small delete-and-smoke-test cycle succeeds.

## Per-project adoption gate

Record evidence separately for each consuming project:

- [ ] **Consumption:** agents call `unbramble` during real work without extra prompting.
- [ ] **Freshness:** the watcher updates the index without agent labor, and stale state is never silent.
- [ ] **Selectivity:** queries return a focused result instead of forcing the agent to reconstruct the same answer with broad searches.

Until all three are demonstrated, keep UnBramble usage explicit for that project.
