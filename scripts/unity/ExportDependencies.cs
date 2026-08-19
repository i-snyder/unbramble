// UnBramble validation spec material -- NOT compiled by this solution (no .csproj references
// this file; UnBramble.Tests.csproj deliberately globs only src/, not scripts/). This is source
// a human copies into a real Unity project's Assets/Editor/ folder by hand.
//
// Purpose: rg-parity can only verify guid-based references (it needs guid text on disk to grep
// for). UI Toolkit's plain-path reference form
// (.uxml `src="..."`, .uss `url(...)`/`@import`) has no guid text at all -- their only real
// ground truth is Unity's own AssetDatabase. This script exports what Unity itself thinks an
// asset's dependencies are, so scripts/compare-unity-deps.ps1 can check UnBramble's `uses`
// output *contains* them (containment, not equality -- UnBramble may legitimately report extra
// unresolved/external/builtin entries Unity's API omits).
//
// Setup:
//   1. Copy this file into <YourUnityProject>/Assets/Editor/ExportDependencies.cs.
//   2. Edit the AssetPaths list below to point at real assets in that project. Include AT
//      LEAST one .uxml or .uss (the whole reason this script exists) and one .shadergraph
//      (ShaderGraph's guid is buried in escaped JSON -- worth spot-checking against Unity's
//      own resolution too, not just rg-parity's regex). ~5 assets total is plenty.
//   3. In the Unity Editor: Tools > UnBramble > Export Dependencies.
//   4. This writes <YourUnityProject>/unbramble-unity-deps.json (project root, next to Assets/).
//   5. Run scripts/compare-unity-deps.ps1 -ProjectRoot <YourUnityProject> against it.
//
// This file has no external dependencies beyond UnityEditor/UnityEngine -- safe to drop into
// any Unity project without pulling in anything else.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnBramble.Validation
{
    public static class ExportDependencies
    {
        // EDIT ME before running, per the setup instructions above. Paths are project-relative,
        // exactly as they'd appear in AssetDatabase (forward slashes, starting with "Assets/").
        private static readonly string[] AssetPaths =
        {
            "Assets/Prefabs/Player.prefab",
            "Assets/Scenes/Level.unity",
            "Assets/Materials/Rock.mat",
            "Assets/UI/Menu.uxml",
            "Assets/Shaders/Custom.shadergraph",
        };

        [MenuItem("Tools/UnBramble/Export Dependencies")]
        public static void Export()
        {
            var result = new Dictionary<string, List<string>>();

            foreach (var path in AssetPaths)
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    Debug.LogWarning($"[UnBramble] '{path}' was not found in AssetDatabase; skipping. " +
                                      "Fix the AssetPaths list in ExportDependencies.cs.");
                    continue;
                }

                // recursive: false -- direct dependencies only, matching `unbramble uses`'s
                // default (non --transitive) scope. The comparison script does a depth-1
                // containment check; a recursive Unity export would compare apples to oranges.
                var deps = AssetDatabase.GetDependencies(path, recursive: false)
                    .Where(d => d != path)
                    .OrderBy(d => d, System.StringComparer.Ordinal)
                    .ToList();

                result[path] = deps;
            }

            if (result.Count == 0)
            {
                Debug.LogError("[UnBramble] No assets resolved; nothing exported. Check the AssetPaths list.");
                return;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var outPath = Path.Combine(projectRoot, "unbramble-unity-deps.json");
            File.WriteAllText(outPath, ToJson(result), Encoding.UTF8);

            Debug.Log($"[UnBramble] Wrote {outPath} ({result.Count} assets, " +
                      $"{result.Values.Sum(v => v.Count)} total dependency entries).");
        }

        // Hand-rolled JSON (JsonUtility has no Dictionary support and this is intentionally
        // dependency-free spec material) -- shape: { "<path>": ["dep1", "dep2", ...], ... }.
        private static string ToJson(Dictionary<string, List<string>> data)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");

            var keys = data.Keys.ToList();
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                var deps = data[key];

                sb.Append("  ").Append(JsonString(key)).Append(": [");
                for (var j = 0; j < deps.Count; j++)
                {
                    sb.Append(JsonString(deps[j]));
                    if (j < deps.Count - 1)
                    {
                        sb.Append(", ");
                    }
                }

                sb.Append(']');
                if (i < keys.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        private static string JsonString(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
#endif
