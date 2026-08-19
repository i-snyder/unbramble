# Technical Comparison

UnBramble combines Unity's serialized asset relationships and Roslyn-resolved C# relationships in one traversable graph. This document explains where that differs from adjacent Unity tools without treating those tools as interchangeable.

Last reviewed: 2026-08-18.

## How to read this comparison

- **Explicit** means the reference form has a dedicated UnBramble parser, model, or test.
- **Documented** means the other tool's own documentation describes the capability.
- **Not documented as a general graph** doesn't mean the tool can never surface the relationship; it means its published workflow doesn't promise the same project-wide dependency model.
- Extensions and custom tooling can broaden any Editor API. The comparison covers built-in workflows, not everything someone could build around them.

## Capability matrix

| Capability | **UnBramble** | Unity `AssetDatabase.GetDependencies` | Rider / ReSharper Unity | Asset Usage Detector | Dependencies Hunter |
| --- | --- | --- | --- | --- | --- |
| Forward asset dependencies | Direct and transitive | Direct and recursive | Not documented as a general asset graph | Reverse-search oriented | Builds an asset dependency map |
| Reverse asset references | Direct and transitive | Must be derived by querying and inverting assets | Unity-aware Find Usages | Selected assets and scene objects | Reference counts and selected-asset lookup |
| C# semantic references | Roslyn, per Unity compilation unit | No | Yes | No Roslyn graph | No |
| One transitive asset + C# walk | Yes | No | Not documented as a general graph | No | No |
| Serialized UnityEvent → method links | Explicit, including guid-less same-asset bindings | Asset dependency only, not the target method | Documented for event-handler usages | Object-reference search, not a Roslyn method graph | Asset dependency only |
| Unresolved GUID and path references | Preserved and reported with source context | Returns paths for resolved dependencies | Not documented as a project-wide audit | Requires an existing object to search for | Points to a separate missing-reference tool |
| Unreachable assets and code | Conservative build-root reachability with hard availability gates | No | Code inspections, not unified project reachability | No | Assets absent from its detected dependency map |
| Automation | Standalone CLI, JSON/JSONL, explicit exit contracts | Unity Editor API | IDE | Unity Editor UI and scripting API | Unity Editor UI |
| Requires a running Unity Editor | No | Yes | No, but requires Rider; some integrations use Editor data | Yes | Yes |

## Reference forms UnBramble handles explicitly

| Reference form | Why a generic asset search can miss or blur it |
| --- | --- |
| Unity YAML GUID references | References live in text assets while identity lives in sibling `.meta` files; UnBramble retains unknown targets instead of dropping broken links |
| Shader Graph asset references | External GUIDs can appear inside backslash-escaped JSON nested in JSON; dashed internal node IDs must not be mistaken for asset GUIDs |
| UI Toolkit references | `.uxml`, `.uss`, and `.tss` can use either GUID query parameters or path forms such as `src`, `url(...)`, and `@import` |
| Assembly definition references | `asmdef` dependencies can be GUIDs, while `precompiledReferences` names a plugin DLL by filename with no GUID or path |
| UnityEvents | A persistent call combines an object reference, method name, and assembly type; common same-asset bindings contain only a local file ID |
| Addressables | Settings and group assets contain their own serialized identity/reference forms; liveness claims are enabled only for versions whose layout has been confirmed |
| Registry package assets | Package contents are indexed as identity targets so ordinary project references don't appear falsely unresolved |
| C# symbols | Roslyn resolves types, overloads, generics, inheritance, calls, and member access per Unity compilation unit, then projects those results into the same file graph |

The complete parser and graph coverage matrix lives in [architecture.md](architecture.md#guid-edge-coverage-matrix-must-catch-forms-confirmed-against-real-unity-output).

## What the other tools are good at

### Unity dependency API

[`AssetDatabase.GetDependencies`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AssetDatabase.GetDependencies.html) is the native choice when writing Editor tooling that needs the resolved assets referenced by one or more input assets. It supports direct and recursive forward queries. Unity notes that its results are referenced assets, not necessarily assets required by a build.

### Rider and ReSharper Unity

[Rider's Unity Find Usages](https://www.jetbrains.com/help/rider/Features_Unity.html#find-usages) provides a rich interactive experience for a developer navigating code. It extends symbol usages with data from scenes, assets, and prefabs and understands Unity event handlers. UnBramble complements it when the consumer is a script or agent, when a walk must continue across multiple asset and code edges, or when the question is project-wide reachability rather than navigation from one symbol.

### Asset Usage Detector

[Asset Usage Detector](https://github.com/yasirkula/UnityAssetUsageDetector) is useful for interactive reverse searches from selected assets or scene objects and exposes a Unity scripting API. Its documentation notes limitations such as `Resources.Load` and, in its scripting/refactoring path, some Assembly Definition and Shader Graph references.

### Dependencies Hunter

[Dependencies Hunter](https://github.com/AlexeyPerov/Unity-Dependencies-Hunter) builds a project asset map primarily from `AssetDatabase.GetDependencies`, offers reverse-reference views, and identifies assets absent from its detected dependency map. It can optionally add Addressables `AssetReference` scanning and Addressables root detection. That's a useful asset-cleanup workflow; UnBramble's `dead-candidates` instead walks a unified asset + code graph from declared build roots and refuses to emit candidates when its safety gates aren't satisfied.

## Limits of UnBramble

UnBramble is static analysis. Reflection, dynamically constructed resource paths, native callbacks, custom asset-bundle build scripts, dependency-injection configuration by name, and other runtime conventions can remain invisible. Semantic C# analysis depends on usable, current Unity-generated project files. Addressables liveness coverage is version-gated.

Every query carries relevant confidence and blind-spot information, and `dead-candidates` emits no candidates at all when its liveness preconditions fail. The full invariants and known gaps are documented in [architecture.md](architecture.md).
