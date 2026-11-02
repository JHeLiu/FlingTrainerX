# FlingTrainerX

A WPF desktop browser for [flingtrainer.com](https://flingtrainer.com/) game trainers —
launcher-style UI, Chinese-localized content, and **in-app downloads** instead of handing you
off to a browser.

![Home](docs/doc_main.png)

![Detail · options](docs/doc_opt.png)

![Detail · downloads](docs/doc_dl.png)

![Download queue](docs/doc_queue.png)

![Favorites](docs/doc_fav.png)

## Features

- **5-per-row card wall with UI virtualization** — `ListBox + VirtualizingStackPanel`
  (`VirtualizationMode=Recycling`, `ScrollUnit=Pixel`); only the visible rows are realized.
- **Infinite scroll** — auto-fetches `/page/N/` when you get near the bottom, appends into the
  last row so the scroll position never jumps.
- **Detail dialog** — banner, badges (`options / version / last updated / published`),
  preview screenshots from the article, and two tabs: **Options** / **Downloads**.
- **Options tab** — grouped option list with hotkey badges, fully Chinese
  (`Edit Money` → `修改金钱`), filter by Chinese name, English name, or hotkey.
- **Downloads tab** — mirrors the site's attachment table (`file / date / size / downloads / action`)
  and downloads **directly into** `%USERPROFILE%\Downloads\FlingTrainerX`, with an inline
  progress bar on the row itself.
- **Download queue** — live progress, speed and ETA per task; cancel / retry / open folder / remove;
  two concurrent downloads, the rest are queued.
- **Favorites** — stored in local SQLite via SqlSugar (`%LOCALAPPDATA%\FlingTrainerX\fling.db`),
  a full JSON snapshot per row so favorites survive cache loss.
- **Sync** — pulls `https://flingtrainer.com/wp-json/wp/v2/posts` and caches the library offline.
- **Browser-grade headers** — the site returns 403 to plain `HttpClient`; requests carry the local
  Edge UA (version read from the registry / `msedge.exe`) plus `sec-ch-ua`, `sec-fetch-*`, etc.
- **PerMonitorV2 DPI awareness** — declared in `app.manifest`, window sized from the monitor's
  physical work area ÷ DPI scale, recomputed on `DpiChanged`.

## How downloads work

The site bounces you through three hops and **never sends `Content-Length`**:

```
/downloads/<slug>/  → 302 /download-trainer.php?...  → 302 .../xxx.exe
                        Content-Type: application/x-msdownload
                        Transfer-Encoding: chunked
```

So the progress bar has two modes: a percentage when the server gives a length, otherwise a
sweeping indeterminate bar plus the bytes received. The size printed on the page (e.g. "931 KB")
is **not** used as the denominator — the real file is 1.7 MB and the bar would stall at 100%.

Streaming write with an 80 KB buffer, `Stopwatch`-based speed, UI refreshed every 120 ms,
`CancellationTokenSource` for cancel, partial files removed on failure.

## Parsing: AngleSharp, no regex

All HTML is parsed into a DOM with AngleSharp 1.9 (`HtmlParser.ParseDocument`, `QuerySelector`,
`QuerySelectorAll`) — the project contains zero regular expressions. Container is narrowed to the
article body and `script/style/noscript/svg` are removed before reading text, which is what used to
leak jQuery source into option names.

## Chinese localization

`Assets/option-zh.tsv` holds 627 exact pairs (option names + group titles) covering the whole site;
`Services/TrainerZh.cs` falls back to prefix/noun composition (`Edit` / `Set` / `Unlimited` ×
`Health` / `Chi` / `Score`) and finally to the original string.

## Project layout

```
FlingTrainerX/
├─ app.manifest            PerMonitorV2 declaration
├─ Assets/                 trainers.json · option-zh.tsv · Covers/ · Posters/
├─ Models/                 TrainerItem · CardRow · DownloadTask
├─ ViewModels/             DownloadRowVm
├─ Services/               TrainerWeb · TrainerZh · DownloadManager · TrainerDataService
│                          AppDatabase (SqlSugar/SQLite) · UserAgentProvider
├─ Converters/             image cache, visibility
├─ Themes/Theme.xaml       colors, cards, tabs, download rows, progress bars
├─ Views/TrainerDetailView detail dialog
└─ MainWindow              sidebar + card wall + queue + settings
```

## Build

```bash
dotnet build FlingTrainerX.slnx -c Release
dotnet run --project FlingTrainerX/FlingTrainerX.csproj
```

Requires the .NET 10 SDK (`net10.0-windows`); NuGet: `SqlSugarCore`, `AngleSharp`.

## Shortcuts

| Key | Action |
| --- | --- |
| `Ctrl + K` | Focus search |
| `Esc` | Close detail dialog |

## Disclaimer

For learning WPF, DOM parsing and download-queue design only. Trainer binaries and game artwork
belong to their respective authors and publishers; the app ships no trainer executables.
