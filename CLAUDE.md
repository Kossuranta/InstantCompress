# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```powershell
dotnet build -c Release                                   # must be 0 warnings
dotnet run -c Release -- --selfcheck ; $LASTEXITCODE      # headless pipeline test; 0 = OK, 1 = fail
dotnet publish -c Release -r win-x64                       # self-contained single-file exe (~93 MB)
```

`--selfcheck` **exit code is the contract** — `OutputType=WinExe`, so on Windows stdout is invisible unless redirected. When launching the published exe from PowerShell it does not block; use `Start-Process -Wait -PassThru` and read `.ExitCode`.

RIDs also published: `linux-x64`, `osx-x64`, `osx-arm64` (see BUILD.md). No trimming — Avalonia + trimming is fragile, ~93 MB is expected.

## Verify before every commit

Build (0 warnings) **and** `--selfcheck` (exit 0). For non-trivial engine logic, add one assert-based check to `SelfCheck.Run()` — there is no test framework; `SelfCheck.cs` is the whole test suite and runs the real production pipeline against temp images. It also directly exercises pure helpers (`Compressor.ApplyOrigin`, `Gather`).

## Architecture

Single Avalonia window, plain code-behind, split into two folders that mirror the load-bearing layer split (same flat `InstantCompress` namespace, no per-folder namespacing):

- **`Engine/` — the engine.** `Compressor.cs` is dispatcher-free and Avalonia-free by design, so `SelfCheck` can run the exact production path headless. Never introduce UI types or `Dispatcher` here. Entry point `CompressBatch` returns a `BatchResult` (output folder + one `FileResult` per input) and runs `Parallel.ForEach` over a `NoBuffering` partitioner (one file index per grab, so a tail of large files can't starve idle cores). Also holds `SelfCheck.cs` and `Settings.cs`.
- **`UI/` — UI + orchestration.** `MainWindow.axaml.cs` owns all thread marshalling. Also holds `App.axaml(.cs)` and `Program.cs`.

Key cross-file mechanics:

- **Coalesced progress.** Workers only `Volatile.Write` the latest `Progress` snapshot (no `Dispatcher.Post` per file — a fast batch would starve the UI thread). A 250 ms `DispatcherTimer` in the window reads and applies it. Per-file *errors* do post individually.
- **File status.** `FileStatus.Ok` / `Failed` / `Skipped`. Undecodable inputs throw `UnsupportedImageException` → **Skipped** (not an error, not reported to `onError`); encode/IO problems → **Failed**. Results are pre-seeded `Skipped`, so any index a cancelled batch never reaches stays `Skipped`.
- **EXIF orientation.** `DecodeOriented` reads `SKCodec.EncodedOrigin` and applies the transform via `ApplyOrigin` before encoding, because the re-encode drops the metadata. Origins 5–8 swap width/height.
- **Output naming.** Names are deduped up front by stem (`a.jpg`+`a.png` → `a.jpg`, `a_1.png`); deriving names per-worker let files clobber each other. Output folder gets a `_1`, `_2` suffix if the timestamped name already exists.
- **RAM-aware concurrency.** `MaxWorkers() = clamp(freeRAM / 400MB, 1, cores)`. Free RAM read natively (`GlobalMemoryStatusEx` / `/proc/meminfo`); macOS or read failure falls back to total memory.
- **Settings** (`Settings.cs`) persist to a JSON file in app-data — no config framework, load/save failures are non-fatal by design.

## Hard constraints

- **SkiaSharp pinned 2.88.9** — Avalonia 11.x targets the 2.88 ABI. Do **not** bump to 3.x. (`SKFilterQuality`, `SKJpegEncoderOptions` etc. are 2.88 APIs.)
- **No TIFF** — Skia ships no TIFF codec. The one supported-input whitelist is `Compressor.SupportedTypes`.
- **No MVVM, DI, config framework, or plugin architecture** — deliberate. Keep it plain code-behind.

## Style

- XML doc comments on public/most members. `<summary>` is always the three-line form; `<param>`/`<returns>` may be single-line when short.
- Use concrete types instead of `var` when the type is not apparent from the right-hand side.
- Comment only when necessary, 2 rows max, and assume an experienced reader. If a longer explanation is needed, extract a method with a `<summary>` instead.
- Local functions: multiline body, declared after the enclosing method's main logic (after the `return`, not before).

## Notes

- `README.md` predates recent features (folder-drop, WebP output, settings/custom-quality/resize, changed default preset) — treat this file as the source of truth for current behavior.
