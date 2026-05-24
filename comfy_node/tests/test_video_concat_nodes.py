import sys
import os
import pytest
import torch
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from video_concat_nodes import (
    VideoCrossFadeTransition,
    VideoTemporalBlend,
    VideoColorMatch,
    AudioCrossFade,
)


def make_video(frames=25, h=64, w=64, c=3, seed=42):
    gen = torch.Generator().manual_seed(seed)
    return torch.rand((frames, h, w, c), generator=gen)


class TestVideoCrossFadeTransition:
    def setup_method(self):
        self.node = VideoCrossFadeTransition()
        self.video_a = make_video(frames=25, seed=1)
        self.video_b = make_video(frames=25, seed=2)

    def test_exclude_overlap_output_length(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "crossfade", include_overlap=False)
        expected = 25 + 25 - trans
        assert result[0].shape[0] == expected

    def test_include_overlap_output_length(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "crossfade", include_overlap=True)
        expected = 25 + 25
        assert result[0].shape[0] == expected

    def test_exclude_overlap_non_transition_unchanged(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "crossfade", include_overlap=False)
        non_trans = 25 - trans
        torch.testing.assert_close(result[0][:non_trans], self.video_a[:non_trans])
        torch.testing.assert_close(result[0][non_trans + trans:], self.video_b[trans:])

    def test_crossfade_start_matches_a(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "crossfade", include_overlap=False)
        non_trans = 25 - trans
        torch.testing.assert_close(result[0][non_trans], self.video_a[non_trans])

    def test_crossfade_after_transition_matches_b(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "crossfade", include_overlap=False)
        non_trans = 25 - trans
        if 25 > trans:
            torch.testing.assert_close(result[0][non_trans + trans:], self.video_b[trans:])

    def test_fade_to_black_exclude_overlap_half_point(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "fade_to_black", include_overlap=False)
        mid = 25 - trans + trans // 2
        assert result[0][mid].max() < 0.5, "Mid-point of fade_to_black should be near black"

    def test_fade_to_black_include_overlap_half_point(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "fade_to_black", include_overlap=True)
        mid_idx = 25 - trans + trans // 2
        assert result[0][mid_idx].max() < 0.5, "Mid-point of fade_to_black (overlap) should be near black"

    def test_fade_to_black_include_overlap_t_ge_half(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "fade_to_black", include_overlap=True)
        base_idx = 25 - trans
        for i in range(trans // 2, trans):
            pixel = result[0][base_idx + i]
            expected_b = self.video_b[i] * ((i / trans - 0.5) * 2)
            torch.testing.assert_close(pixel, expected_b, atol=1e-6, rtol=1e-5)

    def test_fade_to_white_include_overlap_has_bright_frames(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "fade_to_white", include_overlap=True)
        mid_idx = 25 - trans + trans // 2
        assert result[0][mid_idx].min() > 0.8, "Mid-point of fade_to_white should be near white"

    def test_fade_to_white_include_overlap_t_ge_half(self):
        trans = 12
        result = self.node.crossfade(self.video_a, self.video_b, trans, "fade_to_white", include_overlap=True)
        base_idx = 25 - trans
        for i in range(trans // 2, trans, 3):
            pixel = result[0][base_idx + i]
            t = i / trans
            white = torch.ones_like(self.video_a[0])
            expected = white * (1 - (t - 0.5) * 2) + self.video_b[i] * ((t - 0.5) * 2)
            torch.testing.assert_close(pixel, expected, atol=1e-6, rtol=1e-5)

    def test_transition_clamped_to_shorter_video(self):
        short_b = make_video(frames=5, seed=2)
        trans = 12
        result = self.node.crossfade(self.video_a, short_b, trans, "crossfade", include_overlap=False)
        effective_trans = min(12, 25, 5)
        expected = 25 + 5 - effective_trans
        assert result[0].shape[0] == expected

    def test_dissolve_output_shape(self):
        result = self.node.crossfade(self.video_a, self.video_b, 12, "dissolve", include_overlap=False)
        assert result[0].shape == (25 + 25 - 12, 64, 64, 3)

    def test_single_frame_transition(self):
        result = self.node.crossfade(self.video_a, self.video_b, 1, "crossfade", include_overlap=False)
        assert result[0].shape[0] == 25 + 25 - 1

    def test_output_clamped_0_1(self):
        result = self.node.crossfade(self.video_a, self.video_b, 12, "crossfade", include_overlap=False)
        assert result[0].min() >= 0.0
        assert result[0].max() <= 1.0


class TestVideoTemporalBlend:
    def setup_method(self):
        self.node = VideoTemporalBlend()

    def test_zero_strength_no_change(self):
        video = make_video(frames=25)
        result = self.node.temporal_blend(video, 0.0, 12, "transition_only")
        torch.testing.assert_close(result[0], video)

    def test_transition_only_modifies_boundary_frames(self):
        video = make_video(frames=50)
        result = self.node.temporal_blend(video, 0.5, 12, "transition_only")
        mid = result[0][25]
        torch.testing.assert_close(mid, video[25], atol=1e-6, rtol=1e-4)

    def test_transition_only_output_shape(self):
        video = make_video(frames=25)
        result = self.node.temporal_blend(video, 0.5, 12, "transition_only")
        assert result[0].shape == video.shape

    def test_full_video_output_shape(self):
        video = make_video(frames=25)
        result = self.node.temporal_blend(video, 0.5, 12, "full_video")
        assert result[0].shape == video.shape

    def test_full_video_blends_all_frames(self):
        video = make_video(frames=25)
        result = self.node.temporal_blend(video, 0.5, 12, "full_video")
        assert not torch.allclose(result[0], video, atol=1e-6)

    def test_blend_frames_larger_than_video(self):
        video = make_video(frames=5)
        result = self.node.temporal_blend(video, 0.5, 12, "transition_only")
        assert result[0].shape == video.shape


class TestVideoColorMatch:
    def setup_method(self):
        self.node = VideoColorMatch()

    def test_zero_strength_no_change(self):
        video = make_video(frames=10)
        ref = make_video(frames=5, seed=99)
        result = self.node.match_colors(video, ref, 0.0)
        torch.testing.assert_close(result[0], video)

    def test_output_shape_unchanged(self):
        video = make_video(frames=10)
        ref = make_video(frames=5, seed=99)
        result = self.node.match_colors(video, ref, 0.5)
        assert result[0].shape == video.shape

    def test_output_clamped(self):
        video = make_video(frames=10)
        ref = make_video(frames=5, seed=99)
        result = self.node.match_colors(video, ref, 1.0, blend_frames=0)
        assert result[0].min() >= 0.0
        assert result[0].max() <= 1.0

    def test_blend_frames_decay(self):
        video_a = make_video(frames=30, seed=1)
        ref = make_video(frames=5, seed=99)
        result = self.node.match_colors(video_a, ref, 0.8, blend_frames=10)
        first_frame_diff = (result[0][0] - video_a[0]).abs().mean()
        last_frame_diff = (result[0][-1] - video_a[-1]).abs().mean()
        assert first_frame_diff > last_frame_diff

    def test_full_strength_full_match(self):
        video = make_video(frames=10)
        ref = make_video(frames=5, seed=99)
        result = self.node.match_colors(video, ref, 1.0, blend_frames=0)
        assert not torch.allclose(result[0], video, atol=1e-4)


class TestAudioCrossFade:
    def setup_method(self):
        self.node = AudioCrossFade()

    def _make_audio(self, samples=44100, channels=1, freq=440, sr=44100):
        t = torch.linspace(0, samples / sr, samples)
        wave = (0.5 * torch.sin(2 * 3.14159 * freq * t)).unsqueeze(0)
        if channels > 1:
            wave = wave.repeat(channels, 1)
        return {"waveform": wave, "sample_rate": sr}

    def test_mono_crossfade_length(self):
        a = self._make_audio(samples=44100, channels=1)
        b = self._make_audio(samples=44100, channels=1, freq=880)
        result = self.node.crossfade(a, b, 882)
        expected_len = 44100 + 44100 - 882
        assert result[0]["waveform"].shape[-1] == expected_len

    def test_stereo_crossfade_length(self):
        a = self._make_audio(samples=44100, channels=2)
        b = self._make_audio(samples=44100, channels=2, freq=880)
        result = self.node.crossfade(a, b, 882)
        expected_len = 44100 + 44100 - 882
        assert result[0]["waveform"].shape[-1] == expected_len

    def test_zero_crossfade_samples(self):
        a = self._make_audio(samples=44100, channels=1)
        b = self._make_audio(samples=44100, channels=1, freq=880)
        result = self.node.crossfade(a, b, 0)
        torch.testing.assert_close(result[0]["waveform"], a["waveform"])

    def test_non_overlap_preserved(self):
        a = self._make_audio(samples=44100, channels=1)
        b = self._make_audio(samples=44100, channels=1, freq=880)
        crossfade = 882
        result = self.node.crossfade(a, b, crossfade)
        non_overlap = 44100 - crossfade
        result_wave = result[0]["waveform"]
        if result_wave.dim() == 2:
            result_wave = result_wave[0]
        a_wave = a["waveform"]
        if a_wave.dim() == 1:
            a_wave = a_wave.unsqueeze(0)
        torch.testing.assert_close(result_wave[:non_overlap], a_wave[0, :non_overlap], atol=1e-6, rtol=1e-5)

    def test_sample_rate_preserved(self):
        a = self._make_audio(samples=44100, channels=1)
        b = self._make_audio(samples=44100, channels=1, freq=880)
        result = self.node.crossfade(a, b, 882)
        assert result[0]["sample_rate"] == 44100

    def test_crossfade_exceeds_shorter_audio(self):
        a = self._make_audio(samples=100, channels=1)
        b = self._make_audio(samples=44100, channels=1, freq=880)
        result = self.node.crossfade(a, b, 88200)
        expected_len = 100 + 44100 - min(88200, 100, 44100)
        wave = result[0]["waveform"]
        if wave.dim() == 2:
            wave = wave[0]
        assert wave.shape[-1] == expected_len

    def test_stereo_preserves_channel_independence(self):
        a_left = torch.sin(torch.linspace(0, 2 * 3.14159 * 440, 44100)).unsqueeze(0)
        a_right = torch.sin(torch.linspace(0, 2 * 3.14159 * 880, 44100)).unsqueeze(0)
        a_wave = torch.cat([a_left, a_right], dim=0)
        a = {"waveform": a_wave, "sample_rate": 44100}

        b_left = torch.sin(torch.linspace(0, 2 * 3.14159 * 220, 44100)).unsqueeze(0)
        b_right = torch.sin(torch.linspace(0, 2 * 3.14159 * 660, 44100)).unsqueeze(0)
        b_wave = torch.cat([b_left, b_right], dim=0)
        b = {"waveform": b_wave, "sample_rate": 44100}

        result = self.node.crossfade(a, b, 882)
        out = result[0]["waveform"]

        assert out.shape[0] == 2, "Output should have 2 channels"

        left_diff = (out[0] - out[1]).abs().sum().item()
        assert left_diff > 0.1, "Left and right channels should be different (preserved stereo)"

    def test_mono_to_stereo_upmix(self):
        a = self._make_audio(samples=44100, channels=1)
        b = self._make_audio(samples=44100, channels=2, freq=880)
        result = self.node.crossfade(a, b, 882)
        out = result[0]["waveform"]
        assert out.shape[0] == 2, "Output should be stereo when one input is stereo"

    def test_stereo_non_overlap_preserved(self):
        a = self._make_audio(samples=44100, channels=2)
        b = self._make_audio(samples=44100, channels=2, freq=880)
        crossfade = 882
        result = self.node.crossfade(a, b, crossfade)
        non_overlap = 44100 - crossfade
        out = result[0]["waveform"]
        a_wave = a["waveform"]
        torch.testing.assert_close(out[:, :non_overlap], a_wave[:, :non_overlap], atol=1e-6, rtol=1e-5)