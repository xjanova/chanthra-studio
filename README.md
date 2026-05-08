# Chanthra Studio · 자จันทรา

> AI video atelier — a single-window Windows desktop app for the **lunar atelier** workflow:
> generate · edit · score · stitch.
>
> _Chanthra (จันทรา)_ — "moon" in Thai/Sanskrit. Deep-void backgrounds, warm gold accents,
> crimson silk, mauve mid-tones; serif display + IBM Plex.

| | |
|---|---|
| **Stack** | .NET 8 · WPF · MVVM · CommunityToolkit.Mvvm (no source-gen) |
| **DB** | SQLite (Microsoft.Data.Sqlite + Dapper) — portable, sits next to the .exe |
| **Window** | 1640 × 1000, frameless, 12px radius, multi-layer Mica gradient |
| **Status** | Phase 1 — lunar shell + Generate workspace shipping. Other views stubbed. |

## Phases

| # | Scope | Status |
|---|---|---|
| 1 | Foundation: window chrome · sidebar · status bar · Generate view · design tokens | ✅ ship |
| 2 | Data layer · Provider abstractions (LLM + Video) · Settings page (API keys) | next |
| 3 | ComfyUI WebSocket client · live generation flow (queue → progress → save clip) | |
| 4 | Editor · NLE timeline view (clip bin · razor · transitions · render) | |
| 5 | Sound atelier (voice-over + music stems · phoneme strip · FX chain) | |
| 6 | Node Flow (ComfyUI-style graph editor with palette · wires · mini-map) | |
| 7 | Posting (Facebook Graph + generic webhook) · Library · portable build · polish | |

## Build & run

Requires .NET 8 SDK on Windows.

```powershell
dotnet build ChanthraStudio/ChanthraStudio.csproj
dotnet run    --project ChanthraStudio/ChanthraStudio.csproj
```

## Project shape

```
ChanthraStudio/
├── App.xaml                      merge of all themes + value converters
├── MainWindow.xaml               frameless shell + Mica overlays + view switcher
├── Themes/
│   ├── Colors.xaml               void · gold · crimson · moon · text · lines tokens
│   ├── Typography.xaml           Cormorant Garamond · IBM Plex Sans/Thai/Mono
│   ├── Effects.xaml              window shadow · canvas ring · gold glows
│   ├── Icons.xaml                Lucide-derived 24-vb path geometries
│   └── Controls.xaml             pill · chip · slider · toggle · CTA · scrollbar
├── Controls/
│   ├── BrandMark.xaml            crescent moon over thin gold ring
│   ├── TitleBar.xaml             brand · tabs · search · window controls
│   ├── Sidebar.xaml              64px rail with 7 view switches
│   └── StatusBar.xaml            connection · GPU · VRAM · queue · credits
├── Views/
│   ├── GenerateView.xaml         Composer 320 + Stage 1fr + Aside 360
│   └── StubView.xaml             "coming next phase" placeholder
├── ViewModels/
├── Models/
└── Helpers/                      WindowChromeHelper · converters
```

## Provider plan (phase 2)

```
ILlmProvider       Gemini · OpenAI · Anthropic Claude · OpenRouter
IVideoProvider     ComfyUI (WebSocket) · Replicate (Kling/Veo/Sora) · Runway · Pika · fal.ai
IPostingProvider   Facebook Graph API · Generic webhook
```

API keys stored in DPAPI-encrypted settings file next to the executable.

## Design

The high-fidelity reference lives in `chanthra studio.zip` (HTML/CSS/JSX prototype).
All design tokens (colours, type, spacing, shadows) were lifted directly from there.
