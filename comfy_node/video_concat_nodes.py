"""
Video Concatenation nodes for SwarmUI extension.
Version: 2.2.0 - Rewrote VideoFastSave: use imageio_ffmpeg, fix NVENC colors, proper fallback
"""

import torch
import numpy as np
import sys
import os
import io
import struct
import subprocess
import tempfile
import wave
from PIL import Image


__version__ = "2.2.1"


def _get_ffmpeg_path():
    """Get ffmpeg path: prefer imageio_ffmpeg bundled binary, fallback to system ffmpeg."""
    try:
        from imageio_ffmpeg import get_ffmpeg_exe

        ffmpeg_path = get_ffmpeg_exe()
        if os.path.isfile(ffmpeg_path):
            return ffmpeg_path
    except Exception:
        pass
    return "ffmpeg"


_nvenc_available = None


def _check_nvenc_available(ffmpeg_path=None):
    """Check if NVENC encoding is available in the ffmpeg binary. Result is cached."""
    global _nvenc_available
    if _nvenc_available is not None:
        return _nvenc_available
    for path in [ffmpeg_path or _get_ffmpeg_path(), "ffmpeg"]:
        if not path:
            continue
        try:
            result = subprocess.run(
                [path, "-encoders"], capture_output=True, text=True, timeout=10
            )
            if "h264_nvenc" in result.stdout:
                _nvenc_available = True
                return True
        except Exception:
            continue
    _nvenc_available = False
    return False


class VideoColorMatch:
    """
    Matches the color distribution of a video to a reference video.
    Uses per-channel statistics matching. Applied only to the first
    'blend_frames' frames with linear strength decay.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "video": ("IMAGE",),
                "reference": ("IMAGE",),
                "strength": (
                    "FLOAT",
                    {
                        "default": 0.5,
                        "min": 0.0,
                        "max": 1.0,
                        "step": 0.05,
                        "tooltip": "Max strength at the start of the video. Decays linearly to 0 over 'blend_frames'.",
                    },
                ),
                "blend_frames": (
                    "INT",
                    {
                        "default": 0,
                        "min": 0,
                        "max": 500,
                        "step": 1,
                        "tooltip": "How many frames to apply color matching with linear decay. 0 = apply to all frames at full strength.",
                    },
                ),
            },
            "optional": {
                "mode": (
                    ["decay", "constant"],
                    {
                        "default": "decay",
                        "tooltip": "decay: per-frame match fading out over 'blend_frames'. constant: one correction, measured on the first 'source_frames' of the video against the reference, applied to every frame.",
                    },
                ),
                "source_frames": (
                    "INT",
                    {
                        "default": 1,
                        "min": 1,
                        "max": 500,
                        "tooltip": "constant mode: leading frames of 'video' that show the same content as 'reference'.",
                    },
                ),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "match_colors"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Match colors between video sections for visual coherence."

    def match_colors(self, video, reference, strength, blend_frames=0, mode="decay", source_frames=1):
        if strength <= 0:
            return (video,)

        ref_flat = reference.reshape(-1, reference.shape[-1])
        ref_mean = ref_flat.mean(dim=0).reshape(1, 1, 1, -1)
        ref_std = ref_flat.std(dim=0).clamp(min=0.01).reshape(1, 1, 1, -1)

        if mode == "constant":
            src = video[: max(1, min(source_frames, video.shape[0]))]
            src_flat = src.reshape(-1, src.shape[-1])
            src_mean = src_flat.mean(dim=0).reshape(1, 1, 1, -1)
            src_std = src_flat.std(dim=0).clamp(min=0.01).reshape(1, 1, 1, -1)
            matched = (video - src_mean) / src_std * ref_std + ref_mean
            result = video * (1 - strength) + matched * strength
            return (torch.clamp(result, 0.0, 1.0),)

        vid_mean = video.mean(dim=(1, 2), keepdim=True)
        vid_std = video.std(dim=(1, 2), keepdim=True).clamp(min=0.01)

        normalized = (video - vid_mean) / vid_std
        matched = normalized * ref_std + ref_mean

        if blend_frames > 0 and blend_frames < video.shape[0]:
            decay = torch.linspace(strength, 0.0, blend_frames, device=video.device, dtype=video.dtype)
            weight = torch.zeros(video.shape[0], device=video.device, dtype=video.dtype)
            weight[:blend_frames] = decay
            weight = weight.view(-1, 1, 1, 1)
            result = video * (1 - weight) + matched * weight
        else:
            result = video * (1 - strength) + matched * strength

        result = torch.clamp(result, 0.0, 1.0)
        return (result,)


class VideoTemporalBlend:
    """
    Applies temporal smoothing/blending to reduce flickering between frames.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "video": ("IMAGE",),
                "blend_strength": (
                    "FLOAT",
                    {
                        "default": 0.5,
                        "min": 0.0,
                        "max": 1.0,
                        "step": 0.05,
                        "tooltip": "Strength of temporal blending. Higher = more smoothing.",
                    },
                ),
                "blend_frames": (
                    "INT",
                    {
                        "default": 12,
                        "min": 1,
                        "max": 60,
                        "tooltip": "Number of frames affected by blending.",
                    },
                ),
                "mode": (
                    ["transition_only", "full_video"],
                    {
                        "default": "transition_only",
                        "tooltip": "transition_only: blend only near transitions. full_video: blend entire video.",
                    },
                ),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "temporal_blend"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Apply temporal blending to reduce flickering."

    def temporal_blend(
        self, video, blend_strength, blend_frames, mode="transition_only"
    ):
        if blend_strength <= 0:
            return (video,)

        frames = video.shape[0]
        result = video.clone()

        if mode == "transition_only":
            n = min(blend_frames, frames)
            if n > 1:
                indices = torch.arange(1, n, device=video.device, dtype=torch.float32)
                factors = blend_strength * (1.0 - indices / blend_frames) * 0.5
                weight = factors.view(-1, 1, 1, 1)
                result[1:n] = result[1:n] * (1 - weight) + result[:n-1] * weight

            start = max(1, frames - blend_frames)
            if start < frames - 1:
                indices = torch.arange(start, frames - 1, device=video.device, dtype=torch.float32)
                factors = blend_strength * (indices - (frames - blend_frames)) / blend_frames * 0.5
                weight = factors.view(-1, 1, 1, 1)
                result[start:frames-1] = result[start:frames-1] * (1 - weight) + result[start+1:frames] * weight
        else:
            factor = blend_strength * min(1.0, blend_frames / max(1, frames)) * 0.5
            result[1:] = result[1:] * (1 - factor) + result[:-1] * factor

        return (result,)


class VideoCrossFadeTransition:
    """
    Creates a crossfade transition between two video sections.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "video_a": ("IMAGE",),
                "video_b": ("IMAGE",),
                "transition_frames": (
                    "INT",
                    {
                        "default": 12,
                        "min": 1,
                        "max": 60,
                        "tooltip": "Number of frames for the crossfade transition.",
                    },
                ),
                "blend_mode": (
                    ["crossfade", "fade_to_black", "fade_to_white", "dissolve"],
                    {"default": "crossfade"},
                ),
                "include_overlap": (
                    "BOOLEAN",
                    {
                        "default": False,
                        "tooltip": "If True, include overlap frames in output (longer video). If False, exclude overlap (same length as combined).",
                    },
                ),
            },
            "optional": {
                "seam_smooth_strength": (
                    "FLOAT",
                    {
                        "default": 0.0,
                        "min": 0.0,
                        "max": 1.0,
                        "step": 0.05,
                        "tooltip": "Temporal smoothing around the point where video_b's new content starts. 0 = off.",
                    },
                ),
                "seam_smooth_frames": (
                    "INT",
                    {
                        "default": 4,
                        "min": 1,
                        "max": 30,
                        "tooltip": "Frames on each side of the seam affected by the smoothing (linear falloff).",
                    },
                ),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "crossfade"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Create a crossfade transition between two video sections."

    def crossfade(
        self, video_a, video_b, transition_frames, blend_mode, include_overlap=False,
        seam_smooth_strength=0.0, seam_smooth_frames=4,
    ):
        frames_a = video_a.shape[0]
        frames_b = video_b.shape[0]
        trans = min(transition_frames, frames_a, frames_b)

        h, w, c = video_a.shape[1], video_a.shape[2], video_a.shape[3]

        if include_overlap:
            output_frames = frames_a + frames_b
        else:
            output_frames = frames_a + frames_b - trans

        result = torch.zeros((output_frames, h, w, c), dtype=video_a.dtype)

        if include_overlap:
            result[:frames_a] = video_a
            result[frames_a:] = video_b

            for i in range(trans):
                t = i / trans
                idx_a = frames_a - trans + i
                idx_out = frames_a - trans + i

                if blend_mode == "crossfade":
                    result[idx_out] = video_a[idx_a] * (1 - t) + video_b[i] * t
                elif blend_mode == "fade_to_black":
                    if t < 0.5:
                        result[idx_out] = video_a[idx_a] * (1 - t * 2)
                    else:
                        result[idx_out] = video_b[i] * ((t - 0.5) * 2)
                elif blend_mode == "fade_to_white":
                    white = torch.ones_like(video_a[idx_a])
                    if t < 0.5:
                        result[idx_out] = video_a[idx_a] * (1 - t * 2) + white * (t * 2)
                    else:
                        result[idx_out] = white * (
                            1 - (t - 0.5) * 2
                        ) + video_b[i] * ((t - 0.5) * 2)
                elif blend_mode == "dissolve":
                    mask = torch.rand((h, w, c)) < t
                    result[idx_out] = torch.where(mask, video_b[i], video_a[idx_a])
        else:
            non_trans_frames_a = frames_a - trans
            result[:non_trans_frames_a] = video_a[:non_trans_frames_a]

            for i in range(trans):
                t = i / trans

                if blend_mode == "crossfade":
                    result[non_trans_frames_a + i] = (
                        video_a[non_trans_frames_a + i] * (1 - t) + video_b[i] * t
                    )
                elif blend_mode == "fade_to_black":
                    if t < 0.5:
                        result[non_trans_frames_a + i] = video_a[
                            non_trans_frames_a + i
                        ] * (1 - t * 2)
                    else:
                        result[non_trans_frames_a + i] = video_b[i] * ((t - 0.5) * 2)
                elif blend_mode == "fade_to_white":
                    white = torch.ones_like(video_a[non_trans_frames_a + i])
                    if t < 0.5:
                        result[non_trans_frames_a + i] = video_a[
                            non_trans_frames_a + i
                        ] * (1 - t * 2) + white * (t * 2)
                    else:
                        result[non_trans_frames_a + i] = white * (
                            1 - (t - 0.5) * 2
                        ) + video_b[i] * ((t - 0.5) * 2)
                elif blend_mode == "dissolve":
                    mask = torch.rand((h, w, c)) < t
                    result[non_trans_frames_a + i] = torch.where(
                        mask, video_b[i], video_a[non_trans_frames_a + i]
                    )

            result[non_trans_frames_a + trans :] = video_b[trans:]

        seam = frames_a if include_overlap else non_trans_frames_a + trans
        if seam_smooth_strength > 0:
            result = self._smooth_seam(result, seam, seam_smooth_strength, seam_smooth_frames)

        return (result,)

    @staticmethod
    def _smooth_seam(video, seam, strength, radius):
        """Blend each frame near 'seam' with its neighbours, strongest at the seam and fading out over 'radius' frames."""
        frames = video.shape[0]
        src = video.clone()
        for j in range(max(1, seam - radius), min(frames - 1, seam + radius)):
            dist = abs(j + 0.5 - seam)
            w = strength * 0.5 * max(0.0, 1.0 - dist / (radius + 0.5))
            video[j] = src[j] * (1 - w) + (src[j - 1] + src[j + 1]) * 0.5 * w
        return video


class VideoBatch:
    """
    Combines multiple video frames into a single batch.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "video": ("IMAGE",),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "batch"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Batch video frames together."

    def batch(self, video):
        return (video,)


class EmptyLatentVideo:
    """
    Creates an empty latent for video generation.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "width": (
                    "INT",
                    {
                        "default": 512,
                        "min": 64,
                        "max": 2048,
                        "tooltip": "Width of the video.",
                    },
                ),
                "height": (
                    "INT",
                    {
                        "default": 512,
                        "min": 64,
                        "max": 2048,
                        "tooltip": "Height of the video.",
                    },
                ),
                "length": (
                    "INT",
                    {
                        "default": 25,
                        "min": 1,
                        "max": 1000,
                        "tooltip": "Number of frames.",
                    },
                ),
                "batch_size": ("INT", {"default": 1, "min": 1, "max": 100}),
            }
        }

    RETURN_TYPES = ("LATENT",)
    FUNCTION = "generate"
    CATEGORY = "SwarmUI/latent"
    DESCRIPTION = "Create empty latent for video generation."

    def generate(self, width, height, length, batch_size):
        latent = torch.zeros([batch_size, 4, height // 8, width // 8])
        return ({"samples": latent, "length": length},)


class AudioCrossFade:
    """
    Crossfade between two audio tracks during an overlap region.
    This mirrors the video crossfade behavior for smooth audio transitions.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "audio_a": ("AUDIO",),
                "audio_b": ("AUDIO",),
                "crossfade_samples": (
                    "INT",
                    {
                        "default": 882,
                        "min": 1,
                        "max": 88200,
                        "tooltip": "Number of audio samples for crossfade (882 = ~20ms at 44.1kHz)",
                    },
                ),
            },
            "optional": {
                "crossfade_seconds": (
                    "FLOAT",
                    {
                        "default": 0.0,
                        "min": 0.0,
                        "max": 10.0,
                        "step": 0.001,
                        "tooltip": "If > 0, overrides crossfade_samples using the real sample rate.",
                    },
                ),
                "trim_b_seconds": (
                    "FLOAT",
                    {
                        "default": 0.0,
                        "min": 0.0,
                        "max": 10.0,
                        "step": 0.001,
                        "tooltip": "Seconds dropped from the start of audio_b before the crossfade (keeps sync with a video join that drops overlap frames).",
                    },
                ),
            },
        }

    RETURN_TYPES = ("AUDIO",)
    FUNCTION = "crossfade"
    CATEGORY = "SwarmUI/audio"
    DESCRIPTION = "Crossfade two audio tracks. Audio A fades out while Audio B fades in during overlap."

    def crossfade(self, audio_a, audio_b, crossfade_samples, crossfade_seconds=0.0, trim_b_seconds=0.0):
        sample_rate = (
            audio_a.get("sample_rate", 44100) if isinstance(audio_a, dict) else 44100
        )
        if crossfade_seconds > 0:
            crossfade_samples = max(1, int(round(crossfade_seconds * sample_rate)))
        if crossfade_samples <= 0:
            return (audio_a,)

        waveform_a = (
            audio_a.get("waveform", audio_a) if isinstance(audio_a, dict) else audio_a
        )
        waveform_b = (
            audio_b.get("waveform", audio_b) if isinstance(audio_b, dict) else audio_b
        )

        if waveform_a.dim() == 3:
            waveform_a = waveform_a.squeeze(0)
        elif waveform_a.dim() == 1:
            waveform_a = waveform_a.unsqueeze(0)

        if waveform_b.dim() == 3:
            waveform_b = waveform_b.squeeze(0)
        elif waveform_b.dim() == 1:
            waveform_b = waveform_b.unsqueeze(0)

        trim = int(round(trim_b_seconds * sample_rate))
        if 0 < trim < waveform_b.shape[-1]:
            waveform_b = waveform_b[:, trim:]

        samples_a = waveform_a.shape[-1]
        samples_b = waveform_b.shape[-1]
        crossfade = min(crossfade_samples, samples_a, samples_b)

        if crossfade <= 0:
            return (audio_a,)

        num_channels_a = waveform_a.shape[0]
        num_channels_b = waveform_b.shape[0]
        output_channels = max(num_channels_a, num_channels_b)

        if num_channels_a < output_channels:
            waveform_a = waveform_a.repeat(output_channels // num_channels_a, 1)
        if num_channels_b < output_channels:
            waveform_b = waveform_b.repeat(output_channels // num_channels_b, 1)

        non_overlap_a = samples_a - crossfade
        total_samples = samples_a + samples_b - crossfade

        result = torch.zeros(
            (output_channels, total_samples),
            dtype=waveform_a.dtype,
            device=waveform_a.device,
        )

        result[:, :non_overlap_a] = waveform_a[:, :non_overlap_a]

        fade_out = torch.linspace(
            1.0,
            0.0,
            crossfade,
            dtype=waveform_a.dtype,
            device=waveform_a.device,
        )
        fade_in = torch.linspace(
            0.0,
            1.0,
            crossfade,
            dtype=waveform_b.dtype,
            device=waveform_b.device,
        )

        result[:, non_overlap_a:samples_a] = (
            waveform_a[:, non_overlap_a:] * fade_out.unsqueeze(0)
            + waveform_b[:, :crossfade] * fade_in.unsqueeze(0)
        )

        result[:, samples_a:] = waveform_b[:, crossfade:]

        if isinstance(audio_a, dict):
            return ({"waveform": result, "sample_rate": sample_rate},)
        return (result,)


class VideoFastSave:
    """
    Fast video save node using GPU-accelerated encoding (NVENC) when available.
    Falls back to CPU encoding with optimized settings if NVENC is not available.
    Uses imageio_ffmpeg bundled binary for reliability.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "images": ("IMAGE",),
                "fps": (
                    "FLOAT",
                    {"default": 24.0, "min": 0.01, "max": 120.0, "step": 0.01},
                ),
                "quality": (["fast", "balanced", "quality"], {"default": "balanced"}),
                "format": (["h264-mp4", "h265-mp4", "h264_nvenc-mp4", "h265_nvenc-mp4", "webm"], {"default": "h264-mp4"}),
            },
            "optional": {
                "audio": ("AUDIO",),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    OUTPUT_NODE = True
    FUNCTION = "save_video"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Fast video save with GPU acceleration (NVENC) support."

    def save_video(self, images, fps, quality, format, audio=None):
        from server import PromptServer, BinaryEventTypes
        import time as _time

        if images.shape[0] == 0:
            return (images,)

        if images.shape[0] == 1:
            return (images,)

        _t0 = _time.time()

        FFMPEG_PATH = _get_ffmpeg_path()
        use_nvenc = _check_nvenc_available(FFMPEG_PATH)
        ffmpeg_exe = "ffmpeg" if use_nvenc else FFMPEG_PATH

        print(f"[VideoFastSave] FFMPEG_PATH={FFMPEG_PATH}, use_nvenc={use_nvenc}, ffmpeg_exe={ffmpeg_exe}, format={format}", file=sys.stderr)

        quality_settings = {
            "fast": {
                "nvenc_preset": "p1",
                "nvenc_cq": "23",
                "cpu_preset": "ultrafast",
                "cpu_crf": "23",
            },
            "balanced": {
                "nvenc_preset": "p4",
                "nvenc_cq": "20",
                "cpu_preset": "veryfast",
                "cpu_crf": "20",
            },
            "quality": {
                "nvenc_preset": "p7",
                "nvenc_cq": "18",
                "cpu_preset": "medium",
                "cpu_crf": "18",
            },
        }
        settings = quality_settings.get(quality, quality_settings["balanced"])

        images_gpu = (255.0 * images).clamp_(0, 255).to(torch.uint8)
        raw_images = images_gpu.cpu().numpy()
        del images_gpu
        h, w = raw_images.shape[1], raw_images.shape[2]
        print(f"[VideoFastSave] CPU+numpy conversion: {_time.time()-_t0:.2f}s, shape={raw_images.shape}, size={raw_images.nbytes/1048576:.1f}MB", file=sys.stderr)

        # Build base ffmpeg args (same pattern as SwarmSaveAnimationWS)
        args = [
            ffmpeg_exe,
            "-v",
            "error",
            "-f",
            "rawvideo",
            "-pix_fmt",
            "rgb24",
            "-s",
            f"{w}x{h}",
            "-r",
            str(fps),
            "-i",
            "-",
            "-n",
        ]

        audio_input = []
        file_audio = None
        audio_args = []
        video_args = []
        ext = "mp4"
        type_num = 5

        if format == "h264-mp4":
            if use_nvenc:
                video_args = [
                    "-c:v",
                    "h264_nvenc",
                    "-preset",
                    settings["nvenc_preset"],
                    "-cq",
                    settings["nvenc_cq"],
                    "-pix_fmt",
                    "yuv420p",
                    "-color_range",
                    "pc",
                    "-colorspace",
                    "bt709",
                    "-color_primaries",
                    "bt709",
                    "-color_trc",
                    "bt709",
                ]
            else:
                video_args = [
                    "-c:v",
                    "libx264",
                    "-preset",
                    settings["cpu_preset"],
                    "-crf",
                    settings["cpu_crf"],
                    "-pix_fmt",
                    "yuv420p",
                ]
            ext = "mp4"
            type_num = 5
            audio_args = ["-c:a", "aac", "-b:a", "192k"]
        elif format == "h265-mp4":
            if use_nvenc:
                video_args = [
                    "-c:v",
                    "hevc_nvenc",
                    "-preset",
                    settings["nvenc_preset"],
                    "-cq",
                    settings["nvenc_cq"],
                    "-pix_fmt",
                    "yuv420p",
                    "-color_range",
                    "pc",
                    "-colorspace",
                    "bt709",
                    "-color_primaries",
                    "bt709",
                    "-color_trc",
                    "bt709",
                ]
            else:
                video_args = [
                    "-c:v",
                    "libx265",
                    "-preset",
                    settings["cpu_preset"],
                    "-crf",
                    settings["cpu_crf"],
                    "-pix_fmt",
                    "yuv420p",
                ]
            ext = "mp4"
            type_num = 5
            audio_args = ["-c:a", "aac", "-b:a", "192k"]
        elif format == "h264_nvenc-mp4":
            video_args = [
                "-c:v", "h264_nvenc",
                "-preset", settings["nvenc_preset"],
                "-cq", settings["nvenc_cq"],
                "-pix_fmt", "yuv420p",
                "-color_range", "pc",
                "-colorspace", "bt709",
                "-color_primaries", "bt709",
                "-color_trc", "bt709",
            ]
            ext = "mp4"
            type_num = 5
            audio_args = ["-c:a", "aac", "-b:a", "192k"]
        elif format == "h265_nvenc-mp4":
            video_args = [
                "-c:v", "hevc_nvenc",
                "-preset", settings["nvenc_preset"],
                "-cq", settings["nvenc_cq"],
                "-pix_fmt", "yuv420p",
                "-color_range", "pc",
                "-colorspace", "bt709",
                "-color_primaries", "bt709",
                "-color_trc", "bt709",
            ]
            ext = "mp4"
            type_num = 5
            audio_args = ["-c:a", "aac", "-b:a", "192k"]
        elif format == "webm":
            video_args = [
                "-c:v",
                "libvpx-vp9",
                "-crf",
                "20",
                "-b:v",
                "0",
                "-pix_fmt",
                "yuv420p",
            ]
            ext = "webm"
            type_num = 6
            audio_args = ["-c:a", "libopus", "-b:a", "128k"]

        # Prepare audio if present
        if audio is not None and audio_args:
            waveform = (
                audio.get("waveform", audio) if isinstance(audio, dict) else audio
            )
            sample_rate = (
                audio.get("sample_rate", 44100) if isinstance(audio, dict) else 44100
            )

            if waveform.dim() == 3:
                waveform = waveform.squeeze(0)

            channels = waveform.shape[0]
            num_audio_samples = waveform.shape[-1]
            video_duration = len(raw_images) / fps
            target_samples = int(video_duration * sample_rate)

            audio_np = waveform.cpu().numpy()
            if num_audio_samples > target_samples:
                audio_np = audio_np[:, :target_samples]
            elif num_audio_samples < target_samples:
                padding = np.zeros(
                    (channels, target_samples - num_audio_samples), dtype=audio_np.dtype
                )
                audio_np = np.concatenate([audio_np, padding], axis=1)

            import random as _random

            rand = "%016x" % _random.getrandbits(64)
            from folder_paths import get_temp_directory, get_save_image_path

            path = get_save_image_path("swarm_vfs_", get_temp_directory())[0]
            file_audio = os.path.join(path, f"swarm_vfs_{rand}_audio.wav")
            audio_int16 = (np.clip(audio_np.T, -1.0, 1.0) * 32767).astype(np.int16)
            with wave.open(file_audio, "wb") as wav_file:
                wav_file.setnchannels(channels)
                wav_file.setsampwidth(2)
                wav_file.setframerate(sample_rate)
                wav_file.writeframes(audio_int16.tobytes())
            audio_input = ["-i", file_audio]
        else:
            audio_args_list = [] if not audio_args else audio_args
            if audio is None:
                audio_args = []

        # Build output file path
        import random as _random2

        rand2 = "%016x" % _random2.getrandbits(64)
        from folder_paths import get_temp_directory, get_save_image_path as _get_path

        path = _get_path("swarm_vfs_", get_temp_directory())[0]
        file_out = os.path.join(path, f"swarm_vfs_{rand2}.{ext}")

        # Write raw frames to temp file (avoids pipe bottleneck)
        raw_file = os.path.join(path, f"swarm_vfs_{rand2}_raw.rgb")
        _t1 = _time.time()
        with open(raw_file, "wb") as f:
            f.write(raw_images.tobytes())
        print(f"[VideoFastSave] Raw file write: {_time.time()-_t1:.2f}s, file={raw_file}", file=sys.stderr)

        # Use temp file instead of stdin pipe
        idx = args.index("-i")
        args[idx + 1] = raw_file

        # Full ffmpeg command
        cmd = args + audio_input + video_args + audio_args + [file_out]
        print(f"[VideoFastSave] FFmpeg cmd: {' '.join(cmd)}", file=sys.stderr)

        try:
            _t2 = _time.time()
            result = subprocess.run(
                cmd,
                capture_output=True,
                timeout=600,
            )
            print(f"[VideoFastSave] FFmpeg encoding: {_time.time()-_t2:.2f}s, returncode={result.returncode}", file=sys.stderr)

            if result.returncode != 0:
                print(
                    f"[VideoFastSave] ffmpeg failed: {result.stderr.decode('utf-8')}",
                    file=sys.stderr,
                )
                # Fallback: try CPU encoding if NVENC failed
                if use_nvenc and format != "webm":
                    print(
                        "[VideoFastSave] NVENC failed, falling back to CPU encoding",
                        file=sys.stderr,
                    )
                    fallback_crf = settings.get("cpu_crf", "20")
                    fallback_preset = settings.get("cpu_preset", "veryfast")
                    if format in ("h264-mp4", "h264_nvenc-mp4"):
                        fallback_video_args = [
                            "-c:v",
                            "libx264",
                            "-preset",
                            fallback_preset,
                            "-crf",
                            fallback_crf,
                            "-pix_fmt",
                            "yuv420p",
                        ]
                    else:
                        fallback_video_args = [
                            "-c:v",
                            "libx265",
                            "-preset",
                            fallback_preset,
                            "-crf",
                            fallback_crf,
                            "-pix_fmt",
                            "yuv420p",
                        ]
                    fallback_cmd = (
                        [
                            ffmpeg_exe,
                            "-v",
                            "error",
                            "-f",
                            "rawvideo",
                            "-pix_fmt",
                            "rgb24",
                            "-s",
                            f"{w}x{h}",
                            "-r",
                            str(fps),
                            "-i",
                            raw_file,
                            "-n",
                        ]
                        + audio_input
                        + fallback_video_args
                        + (audio_args if audio else [])
                        + [file_out]
                    )

                    result = subprocess.run(
                        fallback_cmd,
                        capture_output=True,
                        timeout=600,
                    )
                    if result.returncode != 0:
                        print(
                            f"[VideoFastSave] Fallback also failed: {result.stderr.decode('utf-8')}",
                            file=sys.stderr,
                        )
                        return (images,)
                else:
                    return (images,)

            # Read output file and send to SwarmUI server
            with open(file_out, "rb") as f:
                out_data = f.read()

            out = io.BytesIO()
            header = struct.pack(">I", type_num)
            out.write(header)
            out.write(out_data)
            out.seek(0)

            server = PromptServer.instance
            server.send_sync(
                "progress", {"value": 12346, "max": 12346}, sid=server.client_id
            )
            server.send_sync(
                BinaryEventTypes.PREVIEW_IMAGE, out.getvalue(), sid=server.client_id
            )

        finally:
            try:
                if os.path.exists(file_out):
                    os.remove(file_out)
            except Exception:
                pass
            try:
                if os.path.exists(raw_file):
                    os.remove(raw_file)
            except Exception:
                pass
            if file_audio:
                try:
                    if os.path.exists(file_audio):
                        os.remove(file_audio)
                except Exception:
                    pass

        return (images,)


class VideoCacheCleanup:
    """
    Cleans up GPU/CPU model caches after video processing.
    Unloads all cached models, frees VRAM, and runs Python garbage collection.
    Should be placed as the last node in the workflow to ensure cleanup after output.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "images": ("IMAGE",),
            },
            "optional": {
                "unload_models": (
                    "BOOLEAN",
                    {
                        "default": True,
                        "tooltip": "Unload all cached models from VRAM.",
                    },
                ),
                "free_memory": (
                    "BOOLEAN",
                    {
                        "default": True,
                        "tooltip": "Free system RAM via garbage collection.",
                    },
                ),
            },
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "cleanup"
    CATEGORY = "SwarmUI/video"
    OUTPUT_NODE = True
    DESCRIPTION = (
        "Clean up model caches after video processing. Unloads models and frees memory."
    )

    def cleanup(self, images, unload_models=True, free_memory=True):
        if unload_models:
            try:
                import comfy.model_management as mm

                mm.unload_all_models()
                mm.cleanup_models()
            except Exception:
                pass

        if free_memory:
            try:
                import gc
                import torch

                gc.collect()
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
                    torch.cuda.synchronize()
            except Exception:
                pass

        return (images,)


class VideoAutoCaption:
    """
    Captions an image using the provided Florence 2 model, concatenates with
    user prompt, encodes with CLIP, and returns conditioning.
    Uses FL2MODEL from Florence2LoadModel for model management compatibility.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "image": ("IMAGE",),
                "prompt": ("STRING", {"default": "", "multiline": True}),
                "clip": ("CLIP",),
                "florence2_model": ("FL2MODEL",),
                "task": (
                    ["more_detailed_caption", "detailed_caption", "caption",
                     "prompt_gen_mixed_caption", "prompt_gen_mixed_caption_plus"],
                    {"default": "more_detailed_caption"},
                ),
            },
            "optional": {
                "max_new_tokens": ("INT", {"default": 128, "min": 32, "max": 512}),
                "num_beams": ("INT", {"default": 3, "min": 1, "max": 16}),
            }
        }

    RETURN_TYPES = ("CONDITIONING",)
    RETURN_NAMES = ("positive",)
    FUNCTION = "encode"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Caption an image with Florence 2, concatenate with prompt, encode with CLIP, return conditioning."

    def encode(self, image, prompt, clip, florence2_model, task,
               max_new_tokens=128, num_beams=3):
        import comfy.model_management as mm
        import gc
        processor = florence2_model['processor']
        patcher = florence2_model['patcher']
        dtype = florence2_model.get('dtype', torch.float16)

        # Offload all other models to free VRAM before loading Florence2
        # This avoids OOM when LTXV or other large models are resident in GPU
        mm.unload_all_models()
        mm.soft_empty_cache()
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

        # Decide device: use CUDA only if enough free VRAM (>3GB), else CPU
        run_device = "cpu"
        if torch.cuda.is_available():
            free_vram = torch.cuda.mem_get_info()[0] / 1024**3
            print(f"[VideoAutoCaption] Free VRAM after offload: {free_vram:.1f}GB", file=sys.stderr)
            if free_vram > 3.0:
                run_device = str(patcher.load_device)

        print(f"[VideoAutoCaption] Running Florence2 on: {run_device}", file=sys.stderr)
        model = patcher.model.to(run_device)

        task_map = {
            "caption": "<CAPTION>",
            "detailed_caption": "<DETAILED_CAPTION>",
            "more_detailed_caption": "<MORE_DETAILED_CAPTION>",
            "prompt_gen_mixed_caption": "<MIXED_CAPTION>",
            "prompt_gen_mixed_caption_plus": "<MIXED_CAPTION_PLUS>",
        }
        task_token = task_map.get(task, "<MORE_DETAILED_CAPTION>")

        image_tensor = image
        if image_tensor.dim() == 4:
            image_tensor = image_tensor[0]
        image_tensor = image_tensor.permute(2, 0, 1)

        inputs = processor(text=task_token, images=image_tensor.unsqueeze(0))

        run_dtype = dtype if run_device != "cpu" else torch.float32
        with torch.no_grad():
            generated_ids = model.generate(
                input_ids=inputs["input_ids"].to(run_device),
                pixel_values=inputs["pixel_values"].to(dtype=run_dtype, device=run_device),
                max_new_tokens=max_new_tokens, num_beams=num_beams, do_sample=num_beams == 1,
            )

        # Move model back to CPU after inference to free VRAM
        patcher.model.to("cpu")
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

        caption = processor.batch_decode(generated_ids, skip_special_tokens=False)[0]
        caption = caption.replace('</s>', '').replace('<s>', '').replace('</s>', '').strip()

        combined = f"{caption}. {prompt.strip()}" if prompt.strip() else caption
        print(f"[VideoAutoCaption] {combined}", file=sys.stderr)

        import nodes
        pos = nodes.CLIPTextEncode().encode(clip, combined)[0]
        return (pos,)


# Node mappings for ComfyUI
NODE_CLASS_MAPPINGS = {
    "VideoColorMatch": VideoColorMatch,
    "VideoTemporalBlend": VideoTemporalBlend,
    "VideoCrossFadeTransition": VideoCrossFadeTransition,
    "VideoBatch": VideoBatch,
    "EmptyLatentVideo": EmptyLatentVideo,
    "AudioCrossFade": AudioCrossFade,
    "VideoFastSave": VideoFastSave,
    "VideoCacheCleanup": VideoCacheCleanup,
    "VideoAutoCaption": VideoAutoCaption,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "VideoColorMatch": "Video Color Match (SwarmUI)",
    "VideoTemporalBlend": "Video Temporal Blend (SwarmUI)",
    "VideoCrossFadeTransition": "Video CrossFade Transition (SwarmUI)",
    "VideoBatch": "Video Batch (SwarmUI)",
    "EmptyLatentVideo": "Empty Latent Video (SwarmUI)",
    "AudioCrossFade": "Audio CrossFade (SwarmUI)",
    "VideoFastSave": "Video Fast Save (SwarmUI)",
    "VideoCacheCleanup": "Video Cache Cleanup (SwarmUI)",
    "VideoAutoCaption": "Video Auto Caption (SwarmUI)",
}
