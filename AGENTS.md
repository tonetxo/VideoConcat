# AGENTS.md — VideoConcat (SwarmUI extension)

This extension lives at `src/Extensions/VideoConcat/` inside the SwarmUI repo. It is **gitignored by SwarmUI** and owns its own git history (remote `origin` = `https://github.com/tonetxo/VideoConcat.git`, branch `main`). Do not look for extension changes in the SwarmUI repo; commit here.

## Architecture (read this first)

- `VideoConcatExtension.cs` — registers the "Video Concatenation" parameter group in `OnPreInit`, the frontend assets (`assets/video-concat.js/.css`), the `comfy_node` folder as Comfy nodes, and one `WorkflowGenerator.AddStep` that builds the extra sections.
- `VideoConcatenator.cs` — core graph building: continuation loop per section (last frame(s) → I2V), color match, temporal blend, crossfade transitions, audio crossfade, optional RTX upscale, final save.
- `CaptionModels.cs` — lists vision models for the "Auto Caption Model" dropdown from local Ollama (`127.0.0.1:11434/api/tags`, filtered by `capabilities` containing `vision`) and llama.cpp server (`127.0.0.1:8080/v1/models`, router mode: only models launched with `--mmproj`). Values are `ollama/<name>` / `llamacpp/<id>`; `CaptionModels.Parse` splits on the first `/`. Cached 30 s, 2 s timeout per server.
- `comfy_node/video_concat_nodes.py` — all custom Comfy nodes (`VideoColorMatch`, `VideoTemporalBlend`, `VideoCrossFadeTransition`, `VideoFastSave`, `VideoCacheCleanup`, `VideoAutoCaption`, ...). Bump `__version__` on node changes.

## Auto Caption

- Florence 2 was removed (v2.3.0). Do not reintroduce `Florence2ModelLoader` / `FL2MODEL`.
- `VideoAutoCaption` calls the LLM server over HTTP with stdlib only (`urllib`), no extra Python deps: Ollama `/api/chat`, llama.cpp `/v1/chat/completions`.
- VRAM is shared with the video model: the node unloads ComfyUI models before the request, and the LLM is unloaded after (`keep_alive: 0` on Ollama, `/models/unload` on llama.cpp router mode). Keep both.
- On any server error the node must fall back to the plain section prompt — never fail the generation.
- The caption is wired by replacing `LTXVConditioning.positive` with the `VideoAutoCaption` output (needs the section's `CLIPTextEncode.clip`).

## Core SwarmUI patches (NVENC)

NVENC output formats need local patches to two core SwarmUI files, protected with `git update-index --skip-worktree`. See `NVENC_PATCH_NOTES.md` before touching video save/format code.

## Build & test

- C#: builds with SwarmUI (`dotnet build src/SwarmUI.csproj --configuration Release` from the SwarmUI root). The full `SwarmUI.sln` build fails on the unrelated `Desktop` project; ignore that.
- C# tests: `Tests/VideoConcatTests.csproj`. Python tests: `comfy_node/tests/` (pytest, `comfy_node/pytest.ini`).
- Live GPU behavior can only be validated by the developer in a running SwarmUI.

## Docs to keep in sync

`README.md` (user docs, English) and `STATE.md` (dev state/changelog, Spanish) — update both when behavior or parameters change.
