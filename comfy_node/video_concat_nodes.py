"""
Video Concatenation nodes for SwarmUI extension.
Version: 2.0.0 - AudioCrossFade with proper channel handling
"""

import torch
import numpy as np


__version__ = "2.0.0"


class VideoColorMatch:
    """
    Matches the color histogram of a video to a reference video.
    Useful for maintaining visual coherence between video sections.
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
                        "tooltip": "How strongly to apply color matching. 1.0 = full match, 0.0 = no change.",
                    },
                ),
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "match_colors"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Match colors between video sections for visual coherence."

    def match_colors(self, video, reference, strength):
        """
        Match video colors to reference using histogram matching.

        Args:
            video: Tensor of shape (frames, height, width, channels)
            reference: Tensor of shape (ref_frames, height, width, channels)
            strength: Float 0-1 for blend strength

        Returns:
            Color-matched video tensor
        """
        if strength <= 0:
            return (video,)

        # Compute global statistics for reference (mean and std per frame)
        # Reference may have fewer frames (just transition frames), so we use global stats
        ref_mean = reference.mean(
            dim=(1, 2), keepdim=True
        )  # Shape: (ref_frames, 1, 1, channels)
        ref_std = (
            reference.std(dim=(1, 2), keepdim=True) + 1e-6
        )  # Shape: (ref_frames, 1, 1, channels)

        # Aggregate to single global statistics
        ref_mean_global = ref_mean.mean(
            dim=0, keepdim=True
        )  # Shape: (1, 1, 1, channels)
        ref_std_global = ref_std.mean(dim=0, keepdim=True)  # Shape: (1, 1, 1, channels)

        # Compute statistics for video (per frame)
        vid_mean = video.mean(
            dim=(1, 2), keepdim=True
        )  # Shape: (frames, 1, 1, channels)
        vid_std = (
            video.std(dim=(1, 2), keepdim=True) + 1e-6
        )  # Shape: (frames, 1, 1, channels)

        # Expand global reference stats to match video frames
        ref_mean_expanded = ref_mean_global.expand(video.shape[0], -1, -1, -1)
        ref_std_expanded = ref_std_global.expand(video.shape[0], -1, -1, -1)

        # Normalize and then apply reference statistics
        normalized = (video - vid_mean) / vid_std
        matched = normalized * ref_std_expanded + ref_mean_expanded

        # Clamp to valid range
        matched = torch.clamp(matched, 0.0, 1.0)

        # Blend with original based on strength
        result = video * (1 - strength) + matched * strength

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
        """
        Apply temporal blending using exponential moving average.

        Args:
            video: Tensor of shape (frames, height, width, channels)
            blend_strength: Float 0-1 for blend strength
            blend_frames: Number of frames for blending
            mode: "transition_only" or "full_video"

        Returns:
            Temporally smoothed video tensor
        """
        if blend_strength <= 0:
            return (video,)

        frames = video.shape[0]
        result = video.clone()

        if mode == "transition_only":
            # Apply blending only in transition zones (beginning and end of video)
            # This is useful for concatenated videos where transitions are at boundaries

            # Blend at the beginning (first blend_frames)
            for i in range(1, min(blend_frames, frames)):
                factor = blend_strength * (1 - i / blend_frames)
                result[i] = result[i] * (1 - factor * 0.5) + result[i - 1] * (
                    factor * 0.5
                )

            # Blend at the end (last blend_frames)
            for i in range(max(1, frames - blend_frames), frames):
                factor = blend_strength * (i - (frames - blend_frames)) / blend_frames
                if i < frames - 1:
                    result[i] = result[i] * (1 - factor * 0.5) + result[i + 1] * (
                        factor * 0.5
                    )
        else:
            # full_video mode: blend across entire video
            for i in range(1, frames):
                # Calculate blend factor based on distance and strength
                factor = blend_strength * min(1.0, blend_frames / max(1, frames))

                # Blend current frame with previous
                result[i] = result[i] * (1 - factor * 0.5) + result[i - 1] * (
                    factor * 0.5
                )

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
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "crossfade"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Create a crossfade transition between two video sections."

    def crossfade(
        self, video_a, video_b, transition_frames, blend_mode, include_overlap=False
    ):
        """
        Crossfade between two videos.

        Args:
            video_a: First video tensor (frames_a, H, W, C)
            video_b: Second video tensor (frames_b, H, W, C)
            transition_frames: Number of frames for transition
            blend_mode: Type of transition
            include_overlap: If True, keep all frames. If False, remove overlap.

        Returns:
            Concatenated video tensor with transition
        """
        frames_a = video_a.shape[0]
        frames_b = video_b.shape[0]
        trans = min(transition_frames, frames_a, frames_b)

        # Get H, W, C from video_a
        h, w, c = video_a.shape[1], video_a.shape[2], video_a.shape[3]

        # Calculate output frames based on mode
        if include_overlap:
            # Include all frames from both videos: video_a plays fully, then video_b plays fully
            # The transition blend happens at the boundary without removing frames
            output_frames = frames_a + frames_b
        else:
            # Exclude overlap: the last 'trans' frames of A blend with first 'trans' frames of B
            # Output: (frames_a - trans) + trans (blend zone) + (frames_b - trans)
            # = frames_a + frames_b - trans
            output_frames = frames_a + frames_b - trans

        # Create output tensor
        result = torch.zeros((output_frames, h, w, c), dtype=video_a.dtype)

        if include_overlap:
            # Mode: include_overlap - play video_a fully, then video_b fully
            # The transition blend is applied at the boundary
            result[:frames_a] = video_a
            result[frames_a:] = video_b

            # Apply crossfade blend at the boundary: last 'trans' of A with first 'trans' of B
            for i in range(trans):
                t = i / trans  # 0 to 1
                idx_a = frames_a - trans + i  # Frame in video_a near end
                idx_out = (
                    frames_a - trans + i
                )  # Position in output (within video_a region)

                if blend_mode == "crossfade":
                    result[idx_out] = video_a[idx_a] * (1 - t) + video_b[i] * t
                elif blend_mode == "fade_to_black":
                    if t < 0.5:
                        result[idx_out] = video_a[idx_a] * (1 - t * 2)
                elif blend_mode == "fade_to_white":
                    white = torch.ones_like(video_a[idx_a])
                    if t < 0.5:
                        result[idx_out] = video_a[idx_a] * (1 - t * 2) + white * (t * 2)
                elif blend_mode == "dissolve":
                    mask = torch.rand((h, w, c)) < t
                    result[idx_out] = torch.where(mask, video_b[i], video_a[idx_a])
        else:
            # Mode: exclude_overlap - blend zone replaces overlap frames
            # Copy video_a up to transition start
            non_trans_frames_a = frames_a - trans
            result[:non_trans_frames_a] = video_a[:non_trans_frames_a]

            # Create transition blend zone
            for i in range(trans):
                t = i / trans  # 0 to 1

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

            # Copy video_b after transition (skip the first 'trans' frames that were blended)
            result[non_trans_frames_a + trans :] = video_b[trans:]

        return (result,)


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
            }
        }

    RETURN_TYPES = ("AUDIO",)
    FUNCTION = "crossfade"
    CATEGORY = "SwarmUI/audio"
    DESCRIPTION = "Crossfade two audio tracks. Audio A fades out while Audio B fades in during overlap."

    def crossfade(self, audio_a, audio_b, crossfade_samples):
        if crossfade_samples <= 0:
            return (audio_a,)

        waveform_a = (
            audio_a.get("waveform", audio_a) if isinstance(audio_a, dict) else audio_a
        )
        waveform_b = (
            audio_b.get("waveform", audio_b) if isinstance(audio_b, dict) else audio_b
        )
        sample_rate = (
            audio_a.get("sample_rate", 44100) if isinstance(audio_a, dict) else 44100
        )

        # ComfyUI audio can be (batch, channels, samples) or (channels, samples)
        # Normalize to (channels, samples)
        if waveform_a.dim() == 3:
            waveform_a = waveform_a.squeeze(0)
        elif waveform_a.dim() == 1:
            waveform_a = waveform_a.unsqueeze(0)

        if waveform_b.dim() == 3:
            waveform_b = waveform_b.squeeze(0)
        elif waveform_b.dim() == 1:
            waveform_b = waveform_b.unsqueeze(0)

        samples_a = waveform_a.shape[-1]
        samples_b = waveform_b.shape[-1]
        crossfade = min(crossfade_samples, samples_a, samples_b)

        if crossfade <= 0:
            return (audio_a,)

        num_channels_a = waveform_a.shape[0]
        num_channels_b = waveform_b.shape[0]

        # Convert to mono by averaging channels
        if num_channels_a > 1:
            waveform_a_mono = waveform_a.mean(dim=0, keepdim=True)
        else:
            waveform_a_mono = waveform_a

        if num_channels_b > 1:
            waveform_b_mono = waveform_b.mean(dim=0, keepdim=True)
        else:
            waveform_b_mono = waveform_b

        # Determine output channels (use the higher channel count)
        output_channels = max(num_channels_a, num_channels_b)

        # Calculate output length
        non_overlap_a = samples_a - crossfade
        total_samples = samples_a + samples_b - crossfade

        # Create mono result first
        result_mono = torch.zeros(
            (1, total_samples),
            dtype=waveform_a_mono.dtype,
            device=waveform_a_mono.device,
        )

        # Copy non-overlapping part of audio_a
        result_mono[0, :non_overlap_a] = waveform_a_mono[0, :non_overlap_a]

        # Create crossfade in overlap region
        fade_out = torch.linspace(
            1.0,
            0.0,
            crossfade,
            dtype=waveform_a_mono.dtype,
            device=waveform_a_mono.device,
        )
        fade_in = torch.linspace(
            0.0,
            1.0,
            crossfade,
            dtype=waveform_b_mono.dtype,
            device=waveform_b_mono.device,
        )

        result_mono[0, non_overlap_a:samples_a] = (
            waveform_a_mono[0, non_overlap_a:] * fade_out
            + waveform_b_mono[0, :crossfade] * fade_in
        )

        # Copy remaining part of audio_b
        result_mono[0, samples_a:] = waveform_b_mono[0, crossfade:]

        # Expand back to output channels if needed
        if output_channels > 1:
            result = result_mono.repeat(output_channels, 1)
        else:
            result = result_mono

        if isinstance(audio_a, dict):
            return ({"waveform": result, "sample_rate": sample_rate},)
        return (result,)


class VideoFastSave:
    """
    Fast video save node using GPU-accelerated encoding (NVENC) when available.
    Falls back to CPU encoding with optimized settings if NVENC is not available.
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
                "format": (["h264-mp4", "h265-mp4", "webm"], {"default": "h264-mp4"}),
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

    def _check_nvenc_available(self):
        """Check if NVENC (NVIDIA GPU encoding) is available."""
        import subprocess

        try:
            result = subprocess.run(
                ["ffmpeg", "-encoders"], capture_output=True, text=True, timeout=5
            )
            return "h264_nvenc" in result.stdout
        except Exception:
            return False

    def save_video(self, images, fps, quality, format, audio=None):
        import subprocess
        import tempfile
        import os
        import wave
        import struct
        import io
        import numpy as np
        from PIL import Image
        from server import PromptServer, BinaryEventTypes

        if images.shape[0] == 0:
            return (images,)

        # For single image, return PNG
        if images.shape[0] == 1:
            return (images,)

        # Quality presets
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

        # Convert tensor to numpy
        i = 255.0 * images.cpu().numpy()
        raw_images = np.clip(i, 0, 255).astype(np.uint8)
        h, w = raw_images.shape[1], raw_images.shape[2]

        # Use NVENC if available, otherwise CPU encoding
        use_nvenc = self._check_nvenc_available()

        # Determine encoder and args
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
                ]
            else:
                video_args = [
                    "-c:v",
                    "libx265",
                    "-preset",
                    settings["cpu_preset"],
                    "-crf",
                    settings["cpu_crf"],
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
        else:
            # Default to h264
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

        # Prepare audio if present
        audio_input = []
        file_audio = None
        if audio is not None and audio_args:
            waveform = (
                audio.get("waveform", audio) if isinstance(audio, dict) else audio
            )
            sample_rate = (
                audio.get("sample_rate", 44100) if isinstance(audio, dict) else 44100
            )

            if waveform.dim() == 3:
                waveform = waveform.squeeze(0)

            num_audio_samples = waveform.shape[-1]
            video_duration = len(raw_images) / fps
            target_samples = int(video_duration * sample_rate)

            audio_np = waveform.cpu().numpy()
            if num_audio_samples > target_samples:
                audio_np = audio_np[:, :target_samples]
            elif num_audio_samples < target_samples:
                channels = audio_np.shape[0]
                padding = np.zeros(
                    (channels, target_samples - num_audio_samples), dtype=audio_np.dtype
                )
                audio_np = np.concatenate([audio_np, padding], axis=1)

            audio_int16 = (np.clip(audio_np.T, -1.0, 1.0) * 32767).astype(np.int16)

            import tempfile

            file_audio = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
            with wave.open(file_audio.name, "wb") as wav_file:
                wav_file.setnchannels(audio_np.shape[0])
                wav_file.setsampwidth(2)
                wav_file.setframerate(sample_rate)
                wav_file.writeframes(audio_int16.tobytes())
            audio_input = ["-i", file_audio.name]

        # Create temporary output file
        import tempfile

        file_out = tempfile.NamedTemporaryFile(suffix=f".{ext}", delete=False)

        # Build ffmpeg command
        ffmpeg_args = (
            [
                "ffmpeg",
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
            ]
            + audio_input
            + video_args
            + audio_args
            + [file_out.name]
        )

        try:
            result = subprocess.run(
                ffmpeg_args,
                input=raw_images.tobytes(),
                capture_output=True,
                timeout=300,  # 5 minute timeout
            )

            if result.returncode != 0:
                print(
                    f"[VideoFastSave] ffmpeg failed: {result.stderr.decode('utf-8')}",
                    file=sys.stderr,
                )
                return (images,)

            # Read output and send to server
            with open(file_out.name, "rb") as f:
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
            # Cleanup
            try:
                os.unlink(file_out.name)
            except Exception:
                pass
            if file_audio:
                try:
                    os.unlink(file_audio.name)
                except Exception:
                    pass

        return (images,)


# Node mappings for ComfyUI
NODE_CLASS_MAPPINGS = {
    "VideoColorMatch": VideoColorMatch,
    "VideoTemporalBlend": VideoTemporalBlend,
    "VideoCrossFadeTransition": VideoCrossFadeTransition,
    "VideoBatch": VideoBatch,
    "EmptyLatentVideo": EmptyLatentVideo,
    "AudioCrossFade": AudioCrossFade,
    "VideoFastSave": VideoFastSave,
}

# Human-readable display names
NODE_DISPLAY_NAME_MAPPINGS = {
    "VideoColorMatch": "Video Color Match (SwarmUI)",
    "VideoTemporalBlend": "Video Temporal Blend (SwarmUI)",
    "VideoCrossFadeTransition": "Video CrossFade Transition (SwarmUI)",
    "VideoBatch": "Video Batch (SwarmUI)",
    "EmptyLatentVideo": "Empty Latent Video (SwarmUI)",
    "AudioCrossFade": "Audio CrossFade (SwarmUI)",
    "VideoFastSave": "Video Fast Save (SwarmUI)",
}
