using System.Collections.Generic;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace VideoConcat;

public class VideoConcatenator
{
    private readonly WorkflowGenerator _generator;
    private JArray _sections;
    private string[] _sectionPrompts;
    private int _transitionFrames = 12;
    private string _transitionMode = "crossfade";
    private string _frameMode = "exclude_overlap";
    private bool _enableColorMatch = true;
    private double _colorStrength = 0.5;
    private bool _enableTemporalBlend = true;
    private double _temporalStrength = 0.5;
    private bool _enableAudioCrossfade = true;
    private int _audioCrossfadeFrames = 8;

    public VideoConcatenator(WorkflowGenerator generator)
    {
        _generator = generator;
    }

    public VideoConcatenator SetSections(JArray sections, string[] sectionPrompts)
    {
        _sections = sections;
        _sectionPrompts = sectionPrompts ?? [];
        return this;
    }

    public VideoConcatenator SetTransitionFrames(int frames)
    {
        _transitionFrames = frames;
        return this;
    }

    public VideoConcatenator SetTransitionMode(string mode)
    {
        _transitionMode = mode ?? "crossfade";
        return this;
    }

    public VideoConcatenator SetFrameMode(string mode)
    {
        _frameMode = mode ?? "exclude_overlap";
        return this;
    }

    public VideoConcatenator SetColorMatching(bool enabled, double strength)
    {
        _enableColorMatch = enabled;
        _colorStrength = strength;
        return this;
    }

    public VideoConcatenator SetTemporalBlending(bool enabled, double strength)
    {
        _enableTemporalBlend = enabled;
        _temporalStrength = strength;
        return this;
    }

    public VideoConcatenator SetAudioCrossfade(bool enabled, int frames)
    {
        _enableAudioCrossfade = enabled;
        _audioCrossfadeFrames = frames;
        return this;
    }

    public void Concatenate()
    {
        if (_sections == null || _sections.Count < 2)
        {
            return;
        }

        T2IModel videoModel = _generator.UserInput.Get(T2IParamTypes.VideoModel, null);
        if (videoModel == null)
        {
            throw new SwarmUserErrorException("Video concatenation requires a Video Model selected in 'Image To Video'");
        }

        _transitionFrames = GetValidTransitionFrames(_transitionFrames, videoModel);

        WGNodeData currentMedia = _generator.CurrentMedia;
        if (currentMedia.Frames == null || currentMedia.Frames < 1)
        {
            Logs.Warning("[VideoConcat] CurrentMedia has no video frames - VideoConcat should run after Image To Video step");
            return;
        }

        WGNodeData originalAudioVae = _generator.CurrentAudioVae;
        WGNodeData originalVae = _generator.CurrentVae;

        int? baseFrames = _generator.UserInput.TryGet(T2IParamTypes.VideoFrames, out int framesRaw) ? framesRaw : null;
        int? baseFps = _generator.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
        double? baseCfg = _generator.UserInput.GetNullable(T2IParamTypes.CFGScale, T2IParamInput.SectionID_Video, false) 
            ?? _generator.UserInput.GetNullable(T2IParamTypes.VideoCFG, T2IParamInput.SectionID_Video);
        int baseSteps = _generator.UserInput.GetNullable(T2IParamTypes.Steps, T2IParamInput.SectionID_Video, false) 
            ?? _generator.UserInput.Get(T2IParamTypes.VideoSteps, 20, sectionId: T2IParamInput.SectionID_Video);
        string negPrompt = _generator.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        long baseSeed = _generator.UserInput.Get(T2IParamTypes.Seed);
        string resFormat = _generator.UserInput.Get(T2IParamTypes.VideoResolution, "Model Preferred");

        int width = videoModel.StandardWidth <= 0 ? 1024 : videoModel.StandardWidth;
        int height = videoModel.StandardHeight <= 0 ? 576 : videoModel.StandardHeight;
        int imageWidth = _generator.UserInput.GetImageWidth();
        int imageHeight = _generator.UserInput.GetImageHeight();
        int resPrecision = videoModel.ModelClass?.CompatClass?.ID == "hunyuan-video" ? 16 : 64;

        if (resFormat == "Image Aspect, Model Res")
        {
            (width, height) = Utilities.ResToModelFit(imageWidth, imageHeight, width * height, resPrecision);
        }
        else if (resFormat == "Image")
        {
            width = imageWidth;
            height = imageHeight;
        }

        JArray widthArr = GetWidthNode();
        JArray heightArr = GetHeightNode();

        WGNodeData previousVideo = currentMedia;
        List<WGNodeData> videoChunks = [currentMedia];
        List<JArray> audioChunks = [];
        
        WGNodeData audioVae = _generator.CurrentAudioVae;
        
        if (currentMedia.AttachedAudio != null)
        {
            WGNodeData firstAudio = currentMedia.AttachedAudio;
            if (firstAudio.DataType == WGNodeData.DT_LATENT_AUDIO && audioVae != null)
            {
                firstAudio = firstAudio.DecodeLatents(audioVae, true);
            }
            if (firstAudio != null && firstAudio.DataType == WGNodeData.DT_AUDIO)
            {
                audioChunks.Add(firstAudio.Path);
            }
        }
        
        int? videoFps = baseFps ?? currentMedia.FPS;

        for (int i = 1; i < _sections.Count; i++)
        {
            JObject section = _sections[i] as JObject;
            int frames = section["duration_frames"]?.Value<int>() ?? baseFrames ?? 25;
            int promptIndex = i - 1;
            string prompt = promptIndex < _sectionPrompts.Length && !string.IsNullOrEmpty(_sectionPrompts[promptIndex]) 
                ? _sectionPrompts[promptIndex] 
                : _generator.UserInput.Get(T2IParamTypes.Prompt, "");
            long sectionSeed = baseSeed + i + 1000;
            double? sectionCfg = baseCfg;
            int sectionSteps = baseSteps;

            WGNodeData newVideo = GenerateContinuationSection(
                videoModel, previousVideo, frames, videoFps ?? 24, sectionSteps, sectionCfg,
                width, height, widthArr, heightArr, prompt, negPrompt, sectionSeed, i
            );

            videoChunks.Add(newVideo);
            
            if (newVideo.AttachedAudio != null && audioVae != null)
            {
                WGNodeData sectionAudio = newVideo.AttachedAudio;
                if (sectionAudio.DataType == WGNodeData.DT_LATENT_AUDIO)
                {
                    sectionAudio = sectionAudio.DecodeLatents(audioVae, true);
                }
                if (sectionAudio != null && sectionAudio.DataType == WGNodeData.DT_AUDIO)
                {
                    audioChunks.Add(sectionAudio.Path);
                }
            }
            
            previousVideo = newVideo;
        }

        if (_enableColorMatch && videoChunks.Count > 1)
        {
            double adjustedStrength = GetAdjustedColorStrength(videoModel, _colorStrength);
            int adjustedRefFrames = GetAdjustedColorRefFrames(videoModel, _transitionFrames);
            
            for (int i = 1; i < videoChunks.Count; i++)
            {
                WGNodeData previousChunk = videoChunks[i - 1];
                WGNodeData currentChunk = videoChunks[i];
                
                JArray refFrames = ExtractLastFrames(previousChunk.Path, adjustedRefFrames);
                
                JArray colorMatched = ApplyColorMatching(currentChunk.Path, refFrames, adjustedStrength);
                videoChunks[i] = currentChunk.WithPath(colorMatched);
            }
        }

        JArray concatenatedVideo = ConcatenateVideoChunks(videoChunks);

        if (_enableTemporalBlend)
        {
            concatenatedVideo = ApplyTemporalBlend(concatenatedVideo, _temporalStrength);
        }
        
        WGNodeData result = previousVideo.WithPath(concatenatedVideo);
        result.FPS = videoFps;
        
        if (audioChunks.Count > 0)
        {
            JArray concatenatedAudio = ConcatenateAudioChunks(audioChunks);
            if (concatenatedAudio != null)
            {
                result.AttachedAudio = new WGNodeData(concatenatedAudio, _generator, WGNodeData.DT_AUDIO, _generator.CurrentCompat());
            }
        }
        
        _generator.CurrentMedia = result;
        
        string outputId = _generator.GetStableDynamicID(50000, 0);
        _generator.CurrentMedia.SaveOutput(originalVae, originalAudioVae, outputId);
        
        Logs.Info($"[VideoConcat] Generated {_sections.Count} sections, concatenated video saved with ID {outputId}");
    }

    private WGNodeData GenerateContinuationSection(
        T2IModel videoModel,
        WGNodeData previousVideo,
        int frames,
        int fps,
        int steps,
        double? cfg,
        int width,
        int height,
        JArray widthArr,
        JArray heightArr,
        string prompt,
        string negPrompt,
        long seed,
        int sectionIndex)
    {
        string frameCountNode = _generator.CreateNode("SwarmCountFrames", new JObject()
        {
            ["image"] = previousVideo.Path
        });
        JArray frameCount = [frameCountNode, 0];

        string fromEndCountNode = _generator.CreateNode("SwarmIntAdd", new JObject()
        {
            ["a"] = frameCount,
            ["b"] = -_transitionFrames
        });
        JArray fromEndCount = [fromEndCountNode, 0];

        string partialBatchNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = previousVideo.Path,
            ["batch_index"] = fromEndCount,
            ["length"] = _transitionFrames
        });
        JArray partialBatch = [partialBatchNode, 0];

        T2IModel videoSwapModel = _generator.UserInput.Get(T2IParamTypes.VideoSwapModel, null);
        double swapPercent = _generator.UserInput.Get(T2IParamTypes.VideoSwapPercent, 0.5);

        WGNodeData inputForSection = previousVideo.WithPath(partialBatch);
        _generator.CurrentMedia = inputForSection;
        
        WorkflowGenerator.ImageToVideoGenInfo genInfo = new()
        {
            Generator = _generator,
            VideoModel = videoModel,
            VideoSwapModel = videoSwapModel,
            VideoSwapPercent = swapPercent,
            Frames = frames,
            VideoCFG = cfg,
            VideoFPS = fps,
            Width = widthArr,
            Height = heightArr,
            Prompt = prompt,
            NegativePrompt = negPrompt,
            Steps = steps,
            Seed = seed,
            BatchIndex = 0,
            BatchLen = _transitionFrames,
            ContextID = T2IParamInput.SectionID_Video
        };

        _generator.CreateImageToVideo(genInfo);
        
        WGNodeData rawResult = _generator.CurrentMedia.AsRawImage(genInfo.Vae);
        
        rawResult.Frames = frames;
        rawResult.FPS = fps;
        
        return rawResult;
    }

    private JArray GetWidthNode()
    {
        string node = _generator.CreateNode("SwarmImageWidth", new JObject()
        {
            ["image"] = _generator.CurrentMedia.Path
        });
        return [node, 0];
    }

    private JArray GetHeightNode()
    {
        string node = _generator.CreateNode("SwarmImageHeight", new JObject()
        {
            ["image"] = _generator.CurrentMedia.Path
        });
        return [node, 0];
    }

    private JArray ExtractLastFrames(JArray video, int frameCount)
    {
        string frameCountNode = _generator.CreateNode("SwarmCountFrames", new JObject()
        {
            ["image"] = video
        });
        JArray totalFrames = [frameCountNode, 0];

        string startIndexNode = _generator.CreateNode("SwarmIntAdd", new JObject()
        {
            ["a"] = totalFrames,
            ["b"] = -frameCount
        });

        string extractNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = video,
            ["batch_index"] = new JArray(startIndexNode, 0),
            ["length"] = frameCount
        });

        return [extractNode, 0];
    }

    private JArray ApplyColorMatching(JArray currentVideo, JArray referenceVideo, double strength)
    {
        string colorMatchNode = _generator.CreateNode("VideoColorMatch", new JObject()
        {
            ["video"] = currentVideo,
            ["reference"] = referenceVideo,
            ["strength"] = strength
        });
        return [colorMatchNode, 0];
    }

    private JArray ConcatenateVideoChunks(List<WGNodeData> chunks)
    {
        if (chunks.Count == 0)
            return null;
        
        if (chunks.Count == 1)
            return chunks[0].Path;

        JArray currentVideo = chunks[0].Path;
        int currentFrames = chunks[0].Frames ?? 0;

        for (int i = 1; i < chunks.Count; i++)
        {
            JArray nextVideo = chunks[i].Path;
            int nextFrames = chunks[i].Frames ?? 0;

            string crossfadeNode = _generator.CreateNode("VideoCrossFadeTransition", new JObject()
            {
                ["video_a"] = currentVideo,
                ["video_b"] = nextVideo,
                ["transition_frames"] = _transitionFrames,
                ["blend_mode"] = _transitionMode,
                ["include_overlap"] = _frameMode == "include_overlap"
            });

            currentVideo = [crossfadeNode, 0];
            
            if (_frameMode == "include_overlap")
            {
                currentFrames = currentFrames + nextFrames;
            }
            else
            {
                currentFrames = currentFrames + nextFrames - _transitionFrames;
            }
        }

        return currentVideo;
    }

    private JArray ApplyTemporalBlend(JArray video, double strength)
    {
        string blendNode = _generator.CreateNode("VideoTemporalBlend", new JObject()
        {
            ["video"] = video,
            ["blend_strength"] = strength,
            ["blend_frames"] = _transitionFrames,
            ["mode"] = "transition_only"
        });
        return [blendNode, 0];
    }

    private JArray ConcatenateAudioChunks(List<JArray> chunks)
    {
        if (chunks.Count == 0)
            return null;
        
        if (chunks.Count == 1)
            return chunks[0];

        if (!_enableAudioCrossfade)
        {
            JArray currentAudio = chunks[0];
            for (int i = 1; i < chunks.Count; i++)
            {
                string concatNode = _generator.CreateNode("AudioConcat", new JObject()
                {
                    ["audio1"] = currentAudio,
                    ["audio2"] = chunks[i],
                    ["direction"] = "after"
                });
                currentAudio = new JArray(concatNode, 0);
            }
            return currentAudio;
        }

        JArray result = chunks[0];
        int? videoFps = _generator.UserInput.TryGet(T2IParamTypes.VideoFPS, out int fpsRaw) ? fpsRaw : null;
        int fps = videoFps ?? 24;
        int audioCrossfadeSamples = _audioCrossfadeFrames * (44100 / fps);
        for (int i = 1; i < chunks.Count; i++)
        {
            string crossfadeNode = _generator.CreateNode("AudioCrossFade", new JObject()
            {
                ["audio_a"] = result,
                ["audio_b"] = chunks[i],
                ["crossfade_samples"] = audioCrossfadeSamples
            });
            result = new JArray(crossfadeNode, 0);
        }
        
        return result;
    }

    private int GetValidTransitionFrames(int frames, T2IModel model)
    {
        string compatClass = model?.ModelClass?.CompatClass?.ID ?? "";

        if (compatClass.StartsWith("wan-21") || compatClass.StartsWith("wan-22"))
        {
            int remainder = (frames - 1) % 4;
            if (remainder == 0)
            {
                return frames;
            }
            int lower = frames - remainder;
            int upper = lower + 4;
            int validFrames = (frames - lower) < (upper - frames) ? lower : upper;
            validFrames = Math.Max(5, validFrames);
            Logs.Info($"[VideoConcat] Adjusted transition frames from {frames} to {validFrames} for Wan model (requires 4n+1 format)");
            return validFrames;
        }

        return frames;
    }

    private double GetAdjustedColorStrength(T2IModel model, double baseStrength)
    {
        string compatClass = model?.ModelClass?.CompatClass?.ID ?? "";

        if (compatClass.StartsWith("wan-21") || compatClass.StartsWith("wan-22"))
        {
            double adjusted = Math.Min(1.0, baseStrength * 1.4);
            Logs.Info($"[VideoConcat] Adjusted color match strength from {baseStrength:F2} to {adjusted:F2} for Wan model");
            return adjusted;
        }

        return baseStrength;
    }

    private int GetAdjustedColorRefFrames(T2IModel model, int transitionFrames)
    {
        string compatClass = model?.ModelClass?.CompatClass?.ID ?? "";

        if (compatClass.StartsWith("wan-21") || compatClass.StartsWith("wan-22"))
        {
            int adjustedFrames = (int)(transitionFrames * 1.5);
            adjustedFrames = GetValidTransitionFrames(adjustedFrames, model);
            Logs.Info($"[VideoConcat] Adjusted color reference frames from {transitionFrames} to {adjustedFrames} for Wan model");
            return adjustedFrames;
        }

        return transitionFrames;
    }
}