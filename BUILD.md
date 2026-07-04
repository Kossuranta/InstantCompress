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

- Native Skia libs are bundled: `Avalonia.Skia` transitively references `SkiaSharp.NativeAssets.Linux` and `HarfBuzzSharp.NativeAssets.Linux`, so `libSkiaSharp.so` / `libHarfBuzzSharp.so` are embedded in the single file via `IncludeNativeLibrariesForSelfExtract`. If a future Avalonia bump ever drops the transitive reference, add an explicit `<PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="2.88.9" />`.
- An X11 desktop is required (`libX11`, `libICE`, `libSM`).
- `fontconfig` and at least one font must be installed (the Skia/HarfBuzz text stack needs them), e.g. `apt install fontconfig fonts-dejavu-core`.
- You may need `chmod +x InstantCompress` after copying.

### macOS

`osx-x64` / `osx-arm64` are documented but **untested** — no Mac in the loop. There is no mac-only code, so the same publish command produces a bare executable you run from a terminal. No `.app` bundle, no signing, no notarization is provided.

## Package versions

- Avalonia 11.3.18 (+ Desktop, Themes.Fluent)
- SkiaSharp 2.88.9 — the exact version Avalonia 11.3.18 binds against. Do **not** bump to SkiaSharp 3.x: Avalonia 11.x targets the 2.88 ABI.
