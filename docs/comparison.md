# Technical Comparison

UnBramble combines Unity's serialized asset relationships and Roslyn-resolved C# relationships in one graph. This comparison covers documented built-in workflows; extensions can broaden any tool.

Last reviewed: 2026-08-18.

## Capabilities

| Capability | **UnBramble** | Unity dependency API | Rider / ReSharper Unity | Asset Usage Detector | Dependencies Hunter |
| --- | --- | --- | --- | --- | --- |
| Forward asset dependencies | Direct and transitive | Direct and recursive | Not a general asset graph | Reverse-search oriented | Asset dependency map |
| Reverse asset references | Direct and transitive | Must be derived | Unity-aware Find Usages | Selected assets and scene objects | Reference counts and lookup |
| C# semantic references | Roslyn, per Unity compilation unit | No | Yes | No Roslyn graph | No |
| One transitive asset + C# walk | Yes | No | Not documented | No | No |
| UnityEvent method links | Explicit, including same-asset bindings | Asset only | Event-handler usages | Object references | Asset only |
| Broken GUID and path references | Preserved with source context | Resolved paths only | No project-wide audit | Requires a searchable object | Separate missing-reference tool |
| Unreachable assets and code | Conservative build-root reachability | No | Code inspections | No | Assets outside its map |
| Agent automation | CLI, JSON/JSONL, explicit exits | Editor API | IDE | Editor UI and API | Editor UI |
| Requires a running Unity Editor | No | Yes | No, but requires Rider | Yes | Yes |

## Reference forms UnBramble models

| Reference form | What needs special handling |
| --- | --- |
| Unity YAML GUIDs | Identity lives in sibling `.meta` files; unresolved targets must remain visible |
| Shader Graphs | Asset GUIDs can sit inside escaped JSON beside unrelated dashed node IDs |
| UI Toolkit | `.uxml`, `.uss`, and `.tss` use both GUID query parameters and paths |
| Assembly definitions | Dependencies can be GUIDs while plugin references are DLL filenames |
| UnityEvents | A call combines object identity, method name, assembly type, and sometimes only a local file ID |
| Addressables | Settings and groups have version-sensitive serialized forms |
| Registry packages | Package assets need identity indexing so project references resolve |
| C# symbols | Roslyn resolves types, overloads, generics, inheritance, calls, and member access |

The complete coverage matrix lives in [architecture.md](architecture.md#guid-edge-coverage-matrix-must-catch-forms-confirmed-against-real-unity-output).

## Adjacent tools

| Tool | Best fit | Where UnBramble differs |
| --- | --- | --- |
| [Unity `AssetDatabase.GetDependencies`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.GetDependencies.html) | Editor tooling that needs Unity-resolved forward asset dependencies | No reverse index, C# graph, or broken-reference inventory |
| [Rider / ReSharper Unity](https://www.jetbrains.com/help/rider/Features_Unity.html#find-usages) | Interactive code navigation and Unity-aware Find Usages | UnBramble is standalone, agent-readable, and walks across asset and code edges |
| [Asset Usage Detector](https://github.com/yasirkula/UnityAssetUsageDetector) | Interactive reverse searches from selected assets or scene objects | Its documented limits include some `Resources.Load`, asmdef, and Shader Graph cases |
| [Dependencies Hunter](https://github.com/AlexeyPerov/Unity-Dependencies-Hunter) | Editor-based asset mapping and cleanup | UnBramble's reachability includes C# and refuses candidates when safety gates fail |

## Limits

UnBramble is static analysis. Reflection, constructed resource paths, native callbacks, custom asset-bundle scripts, dependency injection by name, and other runtime conventions can remain invisible. Semantic C# analysis requires current Unity-generated project files, and Addressables liveness is version-gated.

Queries report relevant confidence and blind spots. `dead-candidates` returns nothing when its liveness preconditions fail. See [architecture.md](architecture.md) for the full guarantees and known gaps.
