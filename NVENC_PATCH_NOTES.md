# VideoConcat Extension — NVENC Patch Notes

This document tracks the local modifications made to enable NVIDIA NVENC hardware encoding for video output in SwarmUI.

## What Was Changed

### 1. `comfy_node/video_concat_nodes.py` (this extension)
- **Added explicit NVENC format options** to `VideoFastSave` node dropdown:
  - `h264_nvenc-mp4`
  - `h265_nvenc-mp4`
- **Fixed `_check_nvenc_available()`** to also probe the system `ffmpeg` (not just the `imageio_ffmpeg` bundled binary, which lacks NVENC support)
- **Replaced stdin pipe with temp-file input** — avoids the ~300-second pipe bottleneck
- **System `ffmpeg` is used** when an `_nvenc` format is selected, `imageio_ffmpeg` otherwise

### 2. `src/Text2Image/T2IParamTypes.cs` (core SwarmUI — protected)
- Added `h264_nvenc-mp4` and `h265_nvenc-mp4` to the `videoFormats` list
- Updated `VideoFormat` and `VideoExtendFormat` descriptions to mention NVENC

### 3. `src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py` (core SwarmUI — protected)
- Added `NVENC_FORMATS = {"h264_nvenc-mp4", "h265_nvenc-mp4"}` constant → `SYSTEM_FFMPEG` used for NVENC formats
- Added `h264_nvenc-mp4` and `h265_nvenc-mp4` to the format dropdown
- Added NVENC encoder args (`h264_nvenc` / `hevc_nvenc` with `-preset p4`)
- **Replaced `subprocess.run(input=raw_images.tobytes())` with `subprocess.Popen` streaming frame-by-frame** — the old approach transferred all frames GPU→CPU in a single `.tobytes()` call (up to **294s** with large models like LTXAV). Streaming each frame individually avoids the peak CPU/RAM bottleneck and lets ffmpeg encode concurrently.
- Width/height extracted from `images[0]` only (not full batch) to avoid unnecessary transfer

## Audio support

All mp4 formats (including NVENC) include audio via AAC: `audio_args = ["-c:a", "aac"]`.
WebM uses libvorbis. WebP/GIF have no audio support.
For LTXAV (video+audio model), always use `h264_nvenc-mp4` or `h264-mp4`.

## Protection with `--skip-worktree`

The two core SwarmUI files above are protected via Git `skip-worktree`. This means:
- `git pull` inside the **SwarmUI root** will **skip** these files
- Your local changes survive SwarmUI updates

### How to verify protection

```bash
cd /home/tonetxo/SwarmUI
git ls-files -v src/Text2Image/T2IParamTypes.cs src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py
```

Both should show an `S` prefix:
```
S src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py
S src/Text2Image/T2IParamTypes.cs
```

## Updating SwarmUI (safe procedure)

> **Important:** `skip-worktree` does NOT prevent merge conflicts when branches have diverged.
> When `git pull` fails with "would be overwritten by merge/checkout", use the full procedure below.

### Normal case (no diverged branches)

When `git pull` works cleanly, skip-worktree protects the files automatically — nothing to do.

### When branches have diverged (ahead N, behind M commits)

```bash
cd /home/tonetxo/SwarmUI

# 1. Backup protected files
cp src/Text2Image/T2IParamTypes.cs /tmp/T2IParamTypes.cs.bak
cp src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py /tmp/SwarmSaveAnimationWS.py.bak

# 2. Unprotect
git update-index --no-skip-worktree src/Text2Image/T2IParamTypes.cs
git update-index --no-skip-worktree src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py

# 3. Stash the two modified files
git stash push src/Text2Image/T2IParamTypes.cs \
    src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py \
    -m "nvenc-patch"

# 4. Pull with merge (not rebase)
git pull --no-rebase

# 5. Restore patch (may open nano for merge commit message — Ctrl+X, Y, Enter)
git stash pop
# If stash pop has conflicts, restore from backup:
# cp /tmp/T2IParamTypes.cs.bak src/Text2Image/T2IParamTypes.cs
# cp /tmp/SwarmSaveAnimationWS.py.bak src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py

# 6. Re-protect
git update-index --skip-worktree src/Text2Image/T2IParamTypes.cs
git update-index --skip-worktree src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py

# 7. Verify
git ls-files -v src/Text2Image/T2IParamTypes.cs \
    src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py
# Both should show S prefix
```

> **Note:** If `T2IParamTypes.cs` was also modified upstream, `git stash pop` will auto-merge it.
> Verify your NVENC entries are still present after the pop.

## Updating this extension (`VideoConcat`)

This extension is a **separate Git repo** inside `src/Extensions/VideoConcat/`.

```bash
cd /home/tonetxo/SwarmUI/src/Extensions/VideoConcat
git pull          # fetch upstream extension updates
git log           # verify your NVENC commit (c3b7c3d) is still there
```

If the upstream extension modified `video_concat_nodes.py`, resolve conflicts as usual:
```bash
cd /home/tonetxo/SwarmUI/src/Extensions/VideoConcat
git pull
# If conflicts:
#   1. edit comfy_node/video_concat_nodes.py to keep both upstream changes and NVENC formats
#   2. git add comfy_node/video_concat_nodes.py
#   3. git commit
```

## Reconciling with upstream (if you ever want to)

If SwarmUI upstream adds new video formats you want, or changes the `VideoFormat` parameter structure:

### Option A — Temporarily unprotect, merge, re-protect

Same as the "diverged branches" procedure above — it handles merging cleanly.

### Option B — Manual cherry-pick

If the upstream changes are minor (e.g. they added one new format string to `videoFormats`), just edit the file by hand to include both the upstream additions and your NVENC entries. No need to unprotect.

## Quick checklist after any update

| Check | Command |
|---|---|
| VideoFastSave still shows nvenc formats? | Generate a video, check dropdown has `h264_nvenc-mp4` |
| SwarmSaveAnimationWS shows nvenc formats? | Check node dropdown in ComfyUI workflow |
| NVENC encode fast? | <1s ffmpeg encode; frame-by-frame GPU→CPU transfer depends on model VRAM pressure |
| Audio included in mp4? | Play output file, verify audio track present |
| Skip-worktree still active? | `git ls-files -v` → both files should have `S` prefix |

## Troubleshooting

### `git pull` says "Your local changes would be overwritten"

skip-worktree does not protect against this when branches have diverged. Use the full
"diverged branches" update procedure above.

### NVENC formats disappeared from dropdown
- Check if `T2IParamTypes.cs` lost the `h264_nvenc-mp4` entries → re-add them
- Check if `video_concat_nodes.py` lost the `h264_nvenc-mp4` INPUT_TYPES entry → re-add them
- Check if `SwarmSaveAnimationWS.py` lost the `NVENC_FORMATS` constant → re-add it

### NVENC encode is slow (>10s)
- Verify `h264_nvenc-mp4` is selected (not `h264-mp4`)
- The current implementation streams frames via Popen stdin — ffmpeg encodes concurrently while receiving data
- With large models (LTXAV, 40GB+ VRAM), the frame-by-frame GPU→CPU transfer takes ~1-2s per frame naturally; check VRAM pressure: the model may be forcing system memory swaps
- Ensure `ffmpeg` system binary still has nvenc: `ffmpeg -encoders | grep nvenc`

### No audio in output video
- Use `h264_nvenc-mp4` or `h264-mp4` — webm/webp do not reliably carry audio through this pipeline
- Verify the audio input is connected in the ComfyUI workflow

### Videos are black
- Check for extension load errors at SwarmUI startup (broken extensions can corrupt the pipeline)
- Verify no broken extensions in `src/Extensions/` — remove or disable any that fail to load
- Check scheduler: AlignYourSteps is not compatible with LTXAV, use `euler` or `dpmpp_2m`

## Files modified in this patch

| File | Repo | Protection | Commit |
|---|---|---|---|
| `comfy_node/video_concat_nodes.py` | `VideoConcat` extension | None (separate repo) | `c3b7c3d` |
| `src/Text2Image/T2IParamTypes.cs` | SwarmUI core | `--skip-worktree` | Uncommitted local |
| `src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py` | SwarmUI core | `--skip-worktree` | Uncommitted local |
