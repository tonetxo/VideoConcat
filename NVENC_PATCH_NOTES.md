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
- Added explicit NVENC format handling with system `ffmpeg`
- Replaced `subprocess.run(input=...)` with `subprocess.Popen` streaming for speed

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

When a new SwarmUI version is released and you run `git pull`:

1. SwarmUI updates everything **except** the two protected files
2. If SwarmUI itself changed `videoFormats` or `VideoFormat` descriptions, Git will **not** report a conflict because the file is skipped
3. **Your old formats list and descriptions remain** — which is fine, they still work
4. If you ever want the *new upstream* formats list, see "Reconciling with upstream" below

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

```bash
cd /home/tonetxo/SwarmUI

# 1. Unprotect
git update-index --no-skip-worktree src/Text2Image/T2IParamTypes.cs
git update-index --no-skip-worktree src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py

# 2. Stash your changes
git stash

# 3. Pull upstream
git pull

# 4. Re-apply your changes (may conflict; resolve manually)
git stash pop

# 5. Re-protect
git update-index --skip-worktree src/Text2Image/T2IParamTypes.cs
git update-index --skip-worktree src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py
```

### Option B — Manual cherry-pick

If the upstream changes are minor (e.g. they added one new format string to `videoFormats`), just edit the file by hand to include both the upstream additions and your NVENC entries, then commit. No need to unprotect.

## Quick checklist after any update

| Check | Command |
|---|---|
| VideoFastSave still shows nvenc formats? | Generate a video, check dropdown has `h264_nvenc-mp4` |
| NVENC still active? | Watch GPU usage during encoding (should spike to ~40-60%) |
| Encoding still fast? | `h264_nvenc-mp4` should encode in ~5-15s, not 300s |
| Skip-worktree still active? | `git ls-files -v` → both files should have `S` prefix |

## Troubleshooting

### `git pull` says "Your local changes would be overwritten"
This should **not** happen for skip-worktree files. If it does, something else was modified. Check:
```bash
git status
```

### NVENC formats disappeared from dropdown
- Check if `T2IParamTypes.cs` lost the `h264_nvenc-mp4` entries → re-add them
- Check if `video_concat_nodes.py` lost the `h264_nvenc-mp4` INPUT_TYPES entry → re-add them

### Encoding is slow again (300s+)
- Verify `h264_nvenc-mp4` is selected (not `h264-mp4`)
- Check logs for `[VideoFastSave]` messages
- Ensure `ffmpeg` system binary still has nvenc: `ffmpeg -encoders \| grep nvenc`

## Files modified in this patch

| File | Repo | Protection | Commit |
|---|---|---|---|
| `comfy_node/video_concat_nodes.py` | `VideoConcat` extension | None (separate repo) | `c3b7c3d` |
| `src/Text2Image/T2IParamTypes.cs` | SwarmUI core | `--skip-worktree` | Uncommitted local |
| `src/BuiltinExtensions/ComfyUIBackend/ExtraNodes/SwarmComfyExtra/SwarmSaveAnimationWS.py` | SwarmUI core | `--skip-worktree` | Uncommitted local |