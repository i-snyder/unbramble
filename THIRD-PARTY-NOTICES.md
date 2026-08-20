# Third-party notices

UnBramble uses the components below. This inventory covers the product dependency graph for the versions currently pinned in the repository; test-only packages are omitted because they aren't part of the distributed CLI.

Release packages contain one generated `LICENSES.md` with UnBramble's MIT license and all required third-party license and attribution text. `scripts/verify-all.ps1` builds it from the checked-in notices and the exact .NET/NativeAOT and Roslyn packages resolved for that publish, de-duplicating identical notice text.

## Runtime components

| Component | Version | License | Copyright / source |
|---|---:|---|---|
| .NET runtime and NativeAOT | 10.0.x | MIT, with bundled third-party notices | .NET Foundation and contributors; https://github.com/dotnet/runtime |
| Microsoft.CodeAnalysis.CSharp / Common (Roslyn) | 5.6.0 | MIT, with bundled third-party notices | .NET Foundation and contributors; https://github.com/dotnet/roslyn |
| Microsoft.Data.Sqlite / Core | 10.0.9 | MIT | Microsoft and .NET Foundation contributors; https://github.com/dotnet/efcore |
| System.Collections.Immutable | 10.0.1 | MIT | .NET Foundation and contributors; https://github.com/dotnet/runtime |
| System.Reflection.Metadata | 10.0.1 | MIT | .NET Foundation and contributors; https://github.com/dotnet/runtime |
| SQLitePCLRaw bundle, configuration, core, and provider | 3.0.3 | Apache-2.0 | Copyright 2014-2025 SourceGear, LLC; https://github.com/ericsink/SQLitePCL.raw |
| SQLite (`e_sqlite3`) | 3.50.4 (`SourceGear.sqlite3` package 3.50.4.5) | Public domain | https://sqlite.org/copyright.html |

The MIT license for Microsoft.Data.Sqlite and the Apache License 2.0 plus upstream NOTICE for SQLitePCLRaw are checked in under `licenses/`. The complete license and attribution text shipped by the .NET runtime, NativeAOT, and Roslyn is preserved in every assembled binary distribution.

## Design references not distributed with UnBramble

`JetBrains/resharper-unity` informed evaluation of Unity serialization forms but isn't installed, linked, copied into the binary, or required at runtime. It's licensed under Apache-2.0: https://github.com/JetBrains/resharper-unity

Unity product names and file-format terminology are used only to describe compatibility. Unity software isn't distributed with UnBramble.
