# Building InstantCompress

## Prerequisites

- .NET SDK 10.0.2xx (built with 10.0.201)

## Dev run

```
dotnet run
```

## Selfcheck (headless pipeline test)

Generates test images in temp, runs the exact production compression pipeline, asserts the outputs exist and are non-empty.

```powershell
dotnet run -c Release -- --selfcheck
$LASTEXITCODE   # 0 = OK, 1 = failure
```

Or against a published exe:

```powershell
.\bin\Release\net10.0\win-x64\publish\InstantCompress.exe --selfcheck
$LASTEXITCODE
```

**The exit code is the contract.** The project is `OutputType=WinExe`, so on Windows `stdout` is invisible unless redirected — always check the exit code.

## Publish (self-contained, single file, no installer)

The csproj enables `SelfContained` + `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` automatically whenever a RID is given. Trimming is deliberately off (Avalonia + trimming is fragile).

```
dotnet publish -c Release -r win-x64
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r osx-x64
dotnet publish -c Release -r osx-arm64
```

Output: `bin/Release/net10.0/<rid>/publish/InstantCompress[.exe]` — one self-contained file (~90 MB), plus a `.pdb` you can ignore/delete.

### Windows

Verified: publish + `--selfcheck` pass. No app.manifest is shipped; default DPI behavior is used.

### Linux

Verified to publish (compile + restore) from Windows; runtime notes:

- Native Skia libs are bundled: `HarfBuzzSharp.NativeAssets.Linux` comes transitively via `Avalonia.Skia`, and `SkiaSharp.NativeAssets.Linux` 4.148.0 is referenced explicitly (Avalonia only pins the 3.119 Linux native transitively — see `## Package versions`), so `libSkiaSharp.so` / `libHarfBuzzSharp.so` are embedded in the single file via `IncludeNativeLibrariesForSelfExtract`. Keep the explicit reference at the same version as the managed `SkiaSharp` package to avoid a managed/native ABI split.
- **Runtime verified on Linux (Avalonia 12 / SkiaSharp 4.148.0 upgrade):** `--selfcheck` exits `0` on Ubuntu 26.04 (WSL2, glibc 2.43), confirming the managed-4.148.0 / native-4.148.0 SkiaSharp ABI works — the full engine pipeline (decode → EXIF-orient → resize via `SKSamplingOptions` → encode jpg/png/webp) runs natively. Re-test with `dotnet publish -c Release -r linux-x64`, then in Linux `chmod +x InstantCompress && ./InstantCompress --selfcheck; echo $?`. `--selfcheck` is headless (no X11/fontconfig needed).
- **`libicu` is required** by the .NET runtime for globalization (`apt install libicu-dev`, or the versioned `libicuNN`). Without it the process aborts at startup with *"Couldn't find a valid ICU package"* — not a Skia issue. Present on most desktop distros; missing on minimal images.
- An X11 desktop is required (`libX11`, `libICE`, `libSM`).
- `fontconfig` and at least one font must be installed (the Skia/HarfBuzz text stack needs them), e.g. `apt install fontconfig fonts-dejavu-core`.
- You may need `chmod +x InstantCompress` after copying.

### macOS

`osx-x64` / `osx-arm64` are documented but **untested** — no Mac in the loop. There is no mac-only code, so the same publish command produces a bare executable you run from a terminal. No `.app` bundle, no signing, no notarization is provided.

## Package versions

- Avalonia 12.0.5 (+ Desktop, Themes.Fluent)
- SkiaSharp 4.148.0 — Avalonia.Skia 12.0.5 depends on `SkiaSharp >= 3.119.4` with no upper bound, so the newer 4.148.0 is pulled explicitly. SkiaSharp 4.x removed the obsolete `SKFilterQuality` enum (resize uses `SKSamplingOptions` now).
- SkiaSharp.NativeAssets.Linux 4.148.0 — explicit, to match the managed package on Linux (Avalonia only pins the 3.119 line transitively).
