"""
Video Concatenation nodes for SwarmUI extension.
Provides video color matching, temporal blending, and concatenation.
"""

import torch
import numpy as np


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
            # Include overlap: frames_a + trans (overlap) + (frames_b - trans)
            output_frames = frames_a + frames_b
        else:
            # Exclude overlap: frames_a + frames_b - trans (overlap removed)
            output_frames = frames_a + frames_b - trans

        # Create output tensor
        result = torch.zeros((output_frames, h, w, c), dtype=video_a.dtype)

        # Copy video_a up to transition start
        non_trans_frames_a = frames_a - trans
        result[:non_trans_frames_a] = video_a[:non_trans_frames_a]

        # Create transition
        for i in range(trans):
            t = i / trans  # 0 to 1

            if blend_mode == "crossfade":
                result[non_trans_frames_a + i] = (
                    video_a[non_trans_frames_a + i] * (1 - t) + video_b[i] * t
                )
            elif blend_mode == "fade_to_black":
                result[non_trans_frames_a + i] = video_a[non_trans_frames_a + i] * (
                    1 - t
                )
            elif blend_mode == "fade_to_white":
                result[non_trans_frames_a + i] = (
                    video_a[non_trans_frames_a + i] * (1 - t)
                    + torch.ones_like(video_a[non_trans_frames_a + i]) * t
                )
            elif blend_mode == "dissolve":
                # Dissolve uses random pixel selection weighted by t
                mask = torch.rand((h, w, c)) < t
                result[non_trans_frames_a + i] = torch.where(
                    mask, video_b[i], video_a[non_trans_frames_a + i]
                )

        # Copy video_b after transition
        if include_overlap:
            # Include overlaps frames from video_b starting from 0
            result[frames_a:] = video_b
        else:
            # Exclude overlap: start from trans frames into video_b
            result[frames_a:] = video_b[trans:]

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


class AudioFade:
    """
    Applies fade in or fade out to audio.
    Uses ComfyUI's native audio processing.
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "audio": ("AUDIO",),
                "fade_length": (
                    "FLOAT",
                    {
                        "default": 0.5,
                        "min": 0.0,
                        "max": 10.0,
                        "step": 0.1,
                        "tooltip": "Duration of the fade in seconds.",
                    },
                ),
                "fade_type": (
                    ["in", "out", "inout"],
                    {
                        "default": "out",
                        "tooltip": "Type of fade: in, out, or both (inout).",
                    },
                ),
            }
        }

    RETURN_TYPES = ("AUDIO",)
    FUNCTION = "apply_fade"
    CATEGORY = "SwarmUI/audio"
    DESCRIPTION = "Apply fade in/out to audio for smooth transitions."

    def apply_fade(self, audio, fade_length, fade_type):
        """
        Apply audio fade.

        Args:
            audio: Audio tensor (waveform)
            fade_length: Duration in seconds
            fade_type: 'in', 'out', or 'inout'

        Returns:
            Faded audio tensor
        """
        if fade_length <= 0:
            return (audio,)

        # Audio is typically shape (channels, samples) or (samples,)
        # Apply envelope multiplication for fade
        waveform = audio.get("waveform", audio) if isinstance(audio, dict) else audio

        # Calculate fade samples
        sample_rate = (
            audio.get("sample_rate", 44100) if isinstance(audio, dict) else 44100
        )
        fade_samples = int(fade_length * sample_rate)

        # Create fade envelope
        if fade_type == "in":
            # Fade in: 0 to 1
            fade_in = torch.linspace(0, 1, min(fade_samples, waveform.shape[-1]))
            envelope = torch.ones(waveform.shape[-1])
            envelope[: len(fade_in)] = fade_in
        elif fade_type == "out":
            # Fade out: 1 to 0
            fade_out = torch.linspace(1, 0, min(fade_samples, waveform.shape[-1]))
            envelope = torch.ones(waveform.shape[-1])
            envelope[-len(fade_out) :] = fade_out
        else:  # inout
            # Both fade in and fade out
            fade_in = torch.linspace(0, 1, min(fade_samples, waveform.shape[-1] // 2))
            fade_out = torch.linspace(1, 0, min(fade_samples, waveform.shape[-1] // 2))
            envelope = torch.ones(waveform.shape[-1])
            envelope[: len(fade_in)] = fade_in
            envelope[-len(fade_out) :] = fade_out

        # Apply envelope
        result = waveform * envelope

        if isinstance(audio, dict):
            return ({"waveform": result, "sample_rate": sample_rate},)
        return (result,)


# Node mappings for ComfyUI
NODE_CLASS_MAPPINGS = {
    "VideoColorMatch": VideoColorMatch,
    "VideoTemporalBlend": VideoTemporalBlend,
    "VideoCrossFadeTransition": VideoCrossFadeTransition,
    "VideoBatch": VideoBatch,
    "EmptyLatentVideo": EmptyLatentVideo,
    "AudioFade": AudioFade,
}

# Human-readable display names
NODE_DISPLAY_NAME_MAPPINGS = {
    "VideoColorMatch": "Video Color Match (SwarmUI)",
    "VideoTemporalBlend": "Video Temporal Blend (SwarmUI)",
    "VideoCrossFadeTransition": "Video CrossFade Transition (SwarmUI)",
    "VideoBatch": "Video Batch (SwarmUI)",
    "EmptyLatentVideo": "Empty Latent Video (SwarmUI)",
    "AudioFade": "Audio Fade (SwarmUI)",
}
