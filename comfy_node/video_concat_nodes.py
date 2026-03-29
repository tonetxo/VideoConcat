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
            reference: Tensor of shape (frames, height, width, channels)
            strength: Float 0-1 for blend strength

        Returns:
            Color-matched video tensor
        """
        if strength <= 0:
            return (video,)

        # Ensure we have compatible shapes
        if reference.shape[0] > video.shape[0]:
            reference = reference[: video.shape[0]]
        elif video.shape[0] > reference.shape[0]:
            reference = torch.nn.functional.interpolate(
                reference.permute(0, 3, 1, 2),
                size=(video.shape[1], video.shape[2]),
                mode="bilinear",
            ).permute(0, 2, 3, 1)
            reference = reference[: video.shape[0]]

        # Compute mean and std for color matching
        ref_mean = reference.mean(dim=(1, 2), keepdim=True)
        ref_std = reference.std(dim=(1, 2), keepdim=True) + 1e-6

        vid_mean = video.mean(dim=(1, 2), keepdim=True)
        vid_std = video.std(dim=(1, 2), keepdim=True) + 1e-6

        # Normalize and then apply reference statistics
        normalized = (video - vid_mean) / vid_std
        matched = normalized * ref_std + ref_mean

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
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "temporal_blend"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Apply temporal blending to reduce flickering."

    def temporal_blend(self, video, blend_strength, blend_frames):
        """
        Apply temporal blending using exponential moving average.

        Args:
            video: Tensor of shape (frames, height, width, channels)
            blend_strength: Float 0-1 for blend strength
            blend_frames: Number of frames for blending

        Returns:
            Temporally smoothed video tensor
        """
        if blend_strength <= 0:
            return (video,)

        frames = video.shape[0]
        result = video.clone()

        # Apply temporal smoothing frame by frame
        for i in range(1, frames):
            # Calculate blend factor based on distance and strength
            factor = blend_strength * min(1.0, blend_frames / frames)

            # Blend current frame with previous
            result[i] = result[i] * (1 - factor * 0.5) + result[i - 1] * (factor * 0.5)

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
            }
        }

    RETURN_TYPES = ("IMAGE",)
    FUNCTION = "crossfade"
    CATEGORY = "SwarmUI/video"
    DESCRIPTION = "Create a crossfade transition between two video sections."

    def crossfade(self, video_a, video_b, transition_frames, blend_mode):
        """
        Crossfade between two videos.

        Args:
            video_a: First video tensor (frames_a, H, W, C)
            video_b: Second video tensor (frames_b, H, W, C)
            transition_frames: Number of frames for transition
            blend_mode: Type of transition

        Returns:
            Concatenated video tensor with transition
        """
        frames_a = video_a.shape[0]
        frames_b = video_b.shape[0]
        trans = min(transition_frames, frames_a, frames_b)

        # Get H, W, C from video_a
        h, w, c = video_a.shape[1], video_a.shape[2], video_a.shape[3]

        # Calculate output frames
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


# Node mappings for ComfyUI
NODE_CLASS_MAPPINGS = {
    "VideoColorMatch": VideoColorMatch,
    "VideoTemporalBlend": VideoTemporalBlend,
    "VideoCrossFadeTransition": VideoCrossFadeTransition,
    "VideoBatch": VideoBatch,
    "EmptyLatentVideo": EmptyLatentVideo,
}

# Human-readable display names
NODE_DISPLAY_NAME_MAPPINGS = {
    "VideoColorMatch": "Video Color Match (SwarmUI)",
    "VideoTemporalBlend": "Video Temporal Blend (SwarmUI)",
    "VideoCrossFadeTransition": "Video CrossFade Transition (SwarmUI)",
    "VideoBatch": "Video Batch (SwarmUI)",
    "EmptyLatentVideo": "Empty Latent Video (SwarmUI)",
}
