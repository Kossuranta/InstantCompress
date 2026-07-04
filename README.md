# InstantCompress

Drop images. Get smaller ones.

A tiny cross-platform desktop app (Avalonia 11, .NET 10) that batch-compresses images to JPG or PNG.

## Features

- Drag & drop any number of images onto the window
- Two knobs only: preset (low / medium / high) and output format (JPG / PNG)
- Compresses in parallel using SkiaSharp native codecs, scaling worker count to the
  machine: as many logical cores as free RAM can back without paging (see below)
- Live progress: counter, bar, current file, byte-weighted ETA
- Cancel mid-batch — partial output is kept, the in-flight file's partial write is deleted
- Per-file errors (corrupt/undecodable inputs) are collected and shown; the batch continues
- Output goes to a new folder `compressed_yyyyMMdd_HHmmss` next to the **first** dropped file
- Follows the OS light/dark theme

## Supported input formats

`jpg` `jpeg` `png` `gif` `bmp` `webp`

**No TIFF** — SkiaSharp/Skia ships no TIFF codec.

## Presets

Hardcoded in code — no config file, no settings persistence, by design.

| Preset | JPEG quality | PNG zlib level |
|--------|--------------|----------------|
| low    | 60           | 6              |
| medium | 75           | 8              |
| high   | 90           | 9              |

## Worker count (RAM-aware)

Concurrency is `min(logical cores, free RAM / 400MB)` — a decoded photo plus native encode
buffers can hold 200-400MB, so the cap picks the conservative end and shrinks automatically
on RAM-starved machines instead of paging. On any normal desktop (a few GB free) this equals
the core count. Free memory is read natively — `GlobalMemoryStatusEx` on Windows,
`/proc/meminfo` `MemAvailable` on Linux — with no added dependency. On macOS, or if either
read fails, it falls back to *total* memory as a best-effort stand-in. See `Compressor.MaxWorkers()`.

## Usage

1. Start the app.
2. Pick a preset and output format (defaults: low, JPG).
3. Drop image files onto the window.
4. Grab the results from the `compressed_...` folder created next to the first input file.

Notes:
- Output name is `{stem}.{format}` — two inputs with the same stem (`a.jpg` + `a.png`) are deduped (`a.jpg`, `a_1.png`).
- Dropped folders are ignored (no recursion).
- Drops are ignored while a job is running.

## Quick build

```
dotnet run
```

See [BUILD.md](BUILD.md) for publishing self-contained single-file executables per platform, and `--selfcheck` for a headless pipeline test.
