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
    private bool _enableRTXUpscale = false;
    private bool _enableFastSave = true;

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

    public VideoConcatenator SetRTXUpscale(bool enabled)
    {
        _enableRTXUpscale = enabled;
        return this;
    }

    public VideoConcatenator SetFastSave(bool enabled)
    {
        _enableFastSave = enabled;
        return this;
    }

    public void Concatenate()
    {
        if (_sections == null || _sections.Count < 2)
        {
            return;
        }

        // Detect if we have a Text2Video model as the main model
        // Text2Video: Model is set and is Text2Video, VideoModel is null
        // Image2Video: VideoModel is set, Model can be anything
        T2IModel videoModel = _generator.UserInput.Get(T2IParamTypes.VideoModel, null);
        T2IModel mainModel = _generator.UserInput.Get(T2IParamTypes.Model, null);
        bool isText2Video = videoModel == null 
            && mainModel != null 
            && mainModel.ModelClass?.CompatClass?.IsText2Video == true;
        
        T2IModel extensionModel = _generator.UserInput.Get(VideoConcatExtension.VideoConcatExtensionModel, null);
        
        Logs.Info($"[VideoConcat] isText2Video={isText2Video}, mainModel={mainModel?.Name ?? "null"}, videoModel={videoModel?.Name ?? "null"}, extensionModel={extensionModel?.Name ?? "null"}");
        
        // For continuations, use Extension Model first, then VideoModel
        T2IModel continuationModel = extensionModel ?? videoModel;
        
        if (continuationModel == null)
        {
            if (isText2Video)
            {
                throw new SwarmUserErrorException(
                    "Video Concatenation with Text2Video models requires an 'Extension Model' " +
                    "(any video model that supports Image2Video: LTXV, Wan I2V, SVD, Hunyuan I2V, Cosmos I2V, etc.).\n" +
                    "Please select an Extension Model in the Video Concatenation group."
                );
            }
            else
            {
                throw new SwarmUserErrorException(
                    "Video concatenation requires a Video Model selected in 'Image To Video', " +
                    "or an 'Extension Model' in the Video Concatenation group."
                );
            }
        }
        
        if (isText2Video)
        {
            Logs.Info($"[VideoConcat] Text2Video mode (main={mainModel.Name}): using Extension Model '{continuationModel.Name}' for continuations");
        }
        else if (extensionModel != null)
        {
            Logs.Info($"[VideoConcat] Image2Video mode: using Extension Model '{extensionModel.Name}' for continuations");
        }
        else
        {
            Logs.Info($"[VideoConcat] Image2Video mode: using Video Model '{videoModel.Name}' for all sections");
        }

        _transitionFrames = GetValidTransitionFrames(_transitionFrames, continuationModel);

        WGNodeData currentMedia = _generator.CurrentMedia;
        Logs.Info($"[VideoConcat] CurrentMedia: DataType={currentMedia.DataType}, Frames={currentMedia.Frames}, Width={currentMedia.Width}, Height={currentMedia.Height}, Path={currentMedia.Path?[0] ?? "null"}");
        
        if (currentMedia.Frames == null || currentMedia.Frames < 1)
        {
            Logs.Warning("[VideoConcat] CurrentMedia has no video frames - VideoConcat should run after video generation");
            return;
        }

        WGNodeData originalAudioVae = _generator.CurrentAudioVae;
        WGNodeData originalVae = _generator.CurrentVae;

        // Get frames from appropriate parameter based on mode
        int? baseFrames = null;
        int? baseFps = null;
        
        if (isText2Video)
        {
            // Text2Video uses Text2VideoFrames
            baseFrames = _generator.UserInput.TryGet(T2IParamTypes.Text2VideoFrames, out int t2vFrames) ? t2vFrames : null;
            baseFps = _generator.UserInput.TryGet(T2IParamTypes.VideoFPS, out int t2vFps) ? t2vFps : null;
        }
        else
        {
            // Image2Video uses VideoFrames
            baseFrames = _generator.UserInput.TryGet(T2IParamTypes.VideoFrames, out int i2vFrames) ? i2vFrames : null;
            baseFps = _generator.UserInput.TryGet(T2IParamTypes.VideoFPS, out int i2vFps) ? i2vFps : null;
        }
        
        // Read CFG: the user sets it globally as CFGScale, and it applies to video generation.
        // Try VideoCFG first (explicit override), then fall back to global CFGScale.
        double? baseCfg = _generator.UserInput.GetNullable(T2IParamTypes.VideoCFG, T2IParamInput.SectionID_Video);
        if (baseCfg == null)
        {
            // Get the effective CFGScale value (includes global/base inheritance)
            baseCfg = _generator.UserInput.GetNullable(T2IParamTypes.CFGScale);
        }
        
        // Get steps: for Text2Video use base Steps, for Image2Video use VideoSteps
        int baseSteps;
        if (isText2Video)
        {
            baseSteps = _generator.UserInput.Get(T2IParamTypes.Steps, 20);
        }
        else
        {
            baseSteps = _generator.UserInput.GetNullable(T2IParamTypes.Steps, T2IParamInput.SectionID_Video, false) 
                ?? _generator.UserInput.Get(T2IParamTypes.VideoSteps, 20, sectionId: T2IParamInput.SectionID_Video);
        }
        string negPrompt = _generator.UserInput.Get(T2IParamTypes.NegativePrompt, "");
        long baseSeed = _generator.UserInput.Get(T2IParamTypes.Seed);
        string resFormat = _generator.UserInput.Get(T2IParamTypes.VideoResolution, "Model Preferred");

        Logs.Info($"[VideoConcat] baseSteps={baseSteps}, baseCfg={baseCfg}, baseFrames={baseFrames}, baseFps={baseFps}, isText2Video={isText2Video}");

        int width = continuationModel.StandardWidth <= 0 ? 1024 : continuationModel.StandardWidth;
        int height = continuationModel.StandardHeight <= 0 ? 576 : continuationModel.StandardHeight;
        int imageWidth = _generator.UserInput.GetImageWidth();
        int imageHeight = _generator.UserInput.GetImageHeight();
        int resPrecision = continuationModel.ModelClass?.CompatClass?.ID == "hunyuan-video" ? 16 : 64;

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
            if (firstAudio.DataType == WGNodeData.DT_LATENT_AUDIO && _generator.CurrentAudioVae != null)
            {
                firstAudio = firstAudio.DecodeLatents(_generator.CurrentAudioVae, true);
            }
            if (firstAudio != null && firstAudio.DataType == WGNodeData.DT_AUDIO)
            {
                audioChunks.Add(firstAudio.Path);
            }
        }
        
        int? videoFps = baseFps ?? currentMedia.FPS;

        // Use the actual frame count of the first video as the default for continuations
        // This ensures all sections match the duration of video 1 even if the parameter differs
        int firstVideoFrames = currentMedia.Frames ?? baseFrames ?? 25;
        Logs.Info($"[VideoConcat] First video has {firstVideoFrames} frames (param default: {baseFrames}), using as default for sections");
        Logs.Info($"[VideoConcat] Starting continuation loop, _sections.Count={_sections.Count}, _sectionPrompts.Length={_sectionPrompts.Length}");
        
        for (int i = 1; i < _sections.Count; i++)
        {
            JObject section = _sections[i] as JObject;
            int frames = section["duration_frames"]?.Value<int>() ?? firstVideoFrames;
            int promptIndex = i - 1;
            string prompt = promptIndex < _sectionPrompts.Length && !string.IsNullOrEmpty(_sectionPrompts[promptIndex]) 
                ? _sectionPrompts[promptIndex] 
                : _generator.UserInput.Get(T2IParamTypes.Prompt, "");
            long sectionSeed = baseSeed + i + 1000;
            double? sectionCfg = baseCfg;
            int sectionSteps = baseSteps;

            Logs.Info($"[VideoConcat] Section {i}: frames={frames}, prompt='{prompt.Substring(0, Math.Min(50, prompt.Length))}...', steps={sectionSteps}, seed={sectionSeed}");

            WGNodeData newVideo = GenerateContinuationSection(
                continuationModel, previousVideo, frames, videoFps ?? 24, sectionSteps, sectionCfg,
                width, height, widthArr, heightArr, prompt, negPrompt, sectionSeed, i
            );

            Logs.Info($"[VideoConcat] Section {i} generated: Frames={newVideo.Frames}, DataType={newVideo.DataType}");
            
            videoChunks.Add(newVideo);
            
            if (newVideo.AttachedAudio != null)
            {
                WGNodeData currentAudioVae = _generator.CurrentAudioVae ?? audioVae;
                WGNodeData sectionAudio = newVideo.AttachedAudio;
                if (sectionAudio.DataType == WGNodeData.DT_LATENT_AUDIO && currentAudioVae != null)
                {
                    sectionAudio = sectionAudio.DecodeLatents(currentAudioVae, true);
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
            string compatClass = continuationModel?.ModelClass?.CompatClass?.ID ?? "";
            bool isWan = compatClass.StartsWith("wan-21") || compatClass.StartsWith("wan-22");
            
            double strength = _colorStrength;
            int refFrameCount = _transitionFrames;
            
            if (isWan)
            {
                strength = Math.Min(1.0, _colorStrength * 1.4);
                refFrameCount = GetValidTransitionFrames((int)(_transitionFrames * 1.5), continuationModel);
                Logs.Info($"[VideoConcat] Wan color match: strength {_colorStrength:F2}->{strength:F2}, refFrames {_transitionFrames}->{refFrameCount}");
            }
            
            for (int i = 1; i < videoChunks.Count; i++)
            {
                WGNodeData previousChunk = videoChunks[i - 1];
                WGNodeData currentChunk = videoChunks[i];
                
                JArray refFrames = ExtractLastFrames(previousChunk.Path, refFrameCount);
                
                JArray colorMatched = ApplyColorMatching(currentChunk.Path, refFrames, strength);
                videoChunks[i] = currentChunk.WithPath(colorMatched);
            }
        }

        JArray concatenatedVideo = ConcatenateVideoChunks(videoChunks, out int totalFrames);

        if (_enableTemporalBlend)
        {
            concatenatedVideo = ApplyTemporalBlend(concatenatedVideo, _temporalStrength);
        }

        // Apply RTX Video Super Resolution before final save (optional, expensive)
        if (_enableRTXUpscale)
        {
            JObject rtxInputs = new JObject()
            {
                ["images"] = concatenatedVideo,
                ["resize_type"] = "scale by multiplier",
                ["resize_type.scale"] = 2.0,
                ["quality"] = "ULTRA"
            };
            Logs.Info($"[VideoConcat] RTX node inputs: {rtxInputs.ToString()}");
            
            string rtxNode = _generator.CreateNode("RTXVideoSuperResolution", rtxInputs);
            concatenatedVideo = new JArray(rtxNode, 0);
            Logs.Info($"[VideoConcat] Created RTXVideoSuperResolution node: {rtxNode}");
        }
        
        WGNodeData result = currentMedia.WithPath(concatenatedVideo);
        result.Frames = totalFrames;
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
        
        // Use VideoFastSave with NVENC support if enabled and RTX upscaling is active (high resolution)
        // VideoFastSave automatically falls back to optimized CPU encoding if NVENC unavailable
        if (_enableFastSave)
        {
            string videoFormat = _generator.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
            string qualityPreset = "balanced";
            
            string fastSaveNode = _generator.CreateNode("VideoFastSave", new JObject()
            {
                ["images"] = result.Path,
                ["fps"] = videoFps,
                ["quality"] = qualityPreset,
                ["format"] = videoFormat,
                ["audio"] = result.AttachedAudio?.Path
            }, outputId);
            
            Logs.Info($"[VideoConcat] Generated {_sections.Count} sections using VideoFastSave (NVENC-accelerated), output ID {outputId}");
        }
        else
        {
            _generator.CurrentMedia.SaveOutput(originalVae, originalAudioVae, outputId);
            Logs.Info($"[VideoConcat] Generated {_sections.Count} sections, concatenated video saved with ID {outputId}");
        }

        // Add cache cleanup node after all processing to free VRAM/RAM
        // This ensures models are unloaded between successive video generations
        string cleanupNode = _generator.CreateNode("VideoCacheCleanup", new JObject()
        {
            ["images"] = result.Path,
            ["unload_models"] = true,
            ["free_memory"] = true,
        }, _generator.GetStableDynamicID(50000, 1));
        
        Logs.Info($"[VideoConcat] Cache cleanup node added: {cleanupNode}");
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
        // Extract the last N frames of the previous video as input for continuation
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
        inputForSection.Frames = frames;
        inputForSection.AttachedAudio = null;

        // Set CurrentMedia to the partial batch (last N frames of previous video)
        // This is the input for the continuation - the model will use these frames as reference
        // Frames must be set to the target duration so that EnsureHasAudioIfNeeded
        // creates LTXVEmptyLatentAudio with the correct frame count for audio generation.
        // AttachedAudio must be cleared so that EnsureHasAudioIfNeeded creates a new empty
        // audio latent for this section instead of reusing the previous section's audio
        // (which has the wrong duration).
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

        // For LTXV models, use LTXVTiledVAEDecode instead of standard VAEDecode/VAEDecodeTiled
        string compatClass = videoModel?.ModelClass?.CompatClass?.ID ?? "";
        bool isLTXV = compatClass.Contains("ltx-video");

        try
        {
            _generator.CreateImageToVideo(genInfo);
        }
        finally
        {
            // Ensure any temp handlers are removed
        }

        WGNodeData result = _generator.CurrentMedia;

        // For LTXV: replace the standard VAE decode node with LTXVTiledVAEDecode.
        // CreateImageToVideo already decoded to DT_VIDEO, so we find the latent that
        // was fed to the decode node and create our own decoder from it.
        Logs.Info($"[VideoConcat] LTXV check: compatClass={compatClass}, isLTXV={isLTXV}, result.DataType={result.DataType}, genInfo.Vae.Path={genInfo.Vae?.Path?.ToString() ?? "null"}");

        if (isLTXV && result.DataType == WGNodeData.DT_VIDEO && genInfo.Vae != null)
        {
            string decodeNodeId = result.Path[0]?.ToString();
            Logs.Info($"[VideoConcat] Attempting VAE decode replacement: decodeNodeId={decodeNodeId}");

            if (!string.IsNullOrEmpty(decodeNodeId))
            {
                JObject decodeNode = _generator.Workflow[decodeNodeId] as JObject;
                Logs.Info($"[VideoConcat] decodeNode found: {decodeNode != null}");

                if (decodeNode != null)
                {
                    JToken samplesInput = decodeNode["inputs"]?["samples"] ?? decodeNode["inputs"]?["latent"];
                    Logs.Info($"[VideoConcat] samplesInput found: {samplesInput != null}, type={decodeNode["inputs"]?.ToString()}");

                    if (samplesInput != null)
                    {
                        string decodeNodeId2 = _generator.CreateNode("LTXVTiledVAEDecode", new JObject()
                        {
                            ["vae"] = genInfo.Vae.Path,
                            ["latents"] = samplesInput,
                            ["horizontal_tiles"] = 1,
                            ["vertical_tiles"] = 1,
                            ["overlap"] = 1,
                            ["last_frame_fix"] = false,
                        });
                        result = result.WithPath([decodeNodeId2, 0], WGNodeData.DT_VIDEO, result.Compat);
                        _generator.CurrentMedia = result;
                        Logs.Info($"[VideoConcat] Section {sectionIndex}: replaced VAE decode with LTXVTiledVAEDecode (nodeId={decodeNodeId2})");
                    }
                    else
                    {
                        Logs.Warning("[VideoConcat] Could not find samples/latents input in decode node");
                    }
                }
                else
                {
                    Logs.Warning($"[VideoConcat] Decode node '{decodeNodeId}' not found in Workflow");
                }
            }
            else
            {
                Logs.Warning("[VideoConcat] decodeNodeId is null or empty");
            }
        }

        // Ensure we have the correct frame count metadata
        result.Frames = frames;
        result.FPS = fps;

        Logs.Info($"[VideoConcat] Section {sectionIndex}: requested {frames} frames, result type={result.DataType}");

        return result;
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

    private JArray ConcatenateVideoChunks(List<WGNodeData> chunks, out int totalFrames)
    {
        totalFrames = 0;
        if (chunks.Count == 0)
            return null;
        
        if (chunks.Count == 1)
        {
            totalFrames = chunks[0].Frames ?? 0;
            return chunks[0].Path;
        }

        JArray currentVideo = chunks[0].Path;
        totalFrames = chunks[0].Frames ?? 0;

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
                totalFrames = totalFrames + nextFrames;
            }
            else
            {
                totalFrames = totalFrames + nextFrames - _transitionFrames;
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
        int audioCrossfadeSamples = (int)Math.Round(_audioCrossfadeFrames * (44100.0 / fps));
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
}