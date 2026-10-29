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
    public record ContinuationModelInfo(T2IModel ContinuationModel, bool IsText2Video);

    public static ContinuationModelInfo ResolveContinuationModel(T2IParamInput userInput, T2IRegisteredParam<T2IModel> extensionModelParam)
    {
        T2IModel mainModel = userInput.Get(T2IParamTypes.Model, null);
        T2IModel videoModel = userInput.Get(T2IParamTypes.VideoModel, null);
        T2IModel extensionModel = userInput.Get(extensionModelParam, null);

        bool isText2Video = videoModel == null
            && mainModel != null
            && mainModel.ModelClass?.CompatClass?.IsText2Video == true;

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

        return new ContinuationModelInfo(continuationModel, isText2Video);
    }

    private readonly WorkflowGenerator _generator;
    private JArray _sections;
    private string[] _sectionPrompts;
    private int _transitionFrames = 12;
    /// <summary>Frames of the previous section that the continuation reproduces at its start (see <see cref="UsesMultiFrameStart"/>).</summary>
    private int _overlapFrames = 1;
    private string _transitionMode = "crossfade";
    private string _frameMode = "exclude_overlap";
    private bool _enableColorMatch = true;
    private double _colorStrength = 0.5;
    private bool _enableTemporalBlend = true;
    private double _temporalStrength = 0.5;
    private bool _enableAudioCrossfade = true;
    private int _audioCrossfadeFrames = 8;
    private bool _enableRTXUpscale = false;
    private bool _enableAutoCaption = false;
    private CaptionModels.Selection _captionModel;
    private string _captionInstruction = "";

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

    public VideoConcatenator SetAutoCaption(bool enabled, CaptionModels.Selection model, string instruction)
    {
        _enableAutoCaption = enabled && model is not null;
        _captionModel = model;
        _captionInstruction = instruction ?? "";
        return this;
    }

    public void Concatenate()
    {
        if (_sections == null || _sections.Count < 2)
        {
            return;
        }

        ContinuationModelInfo modelInfo = ResolveContinuationModel(_generator.UserInput, VideoConcatExtension.VideoConcatExtensionModel);
        T2IModel continuationModel = modelInfo.ContinuationModel;
        bool isText2Video = modelInfo.IsText2Video;

        T2IModel mainModel = _generator.UserInput.Get(T2IParamTypes.Model, null);
        T2IModel videoModel = _generator.UserInput.Get(T2IParamTypes.VideoModel, null);
        T2IModel extensionModel = _generator.UserInput.Get(VideoConcatExtension.VideoConcatExtensionModel, null);
        
        Logs.Info($"[VideoConcat] isText2Video={isText2Video}, mainModel={mainModel?.Name ?? "null"}, videoModel={videoModel?.Name ?? "null"}, extensionModel={extensionModel?.Name ?? "null"}");
        
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
        // Most I2V models are conditioned on a single start image only (core's FixMediaLen keeps frame 0 of the input),
        // so the continuation re-creates exactly one frame of the previous section. Feeding them the last N frames made the
        // continuation start from the N-th last frame, and the crossfade then blended two diverging motions (ghosting + jump).
        _overlapFrames = UsesMultiFrameStart(continuationModel) ? _transitionFrames : 1;
        Logs.Info($"[VideoConcat] Overlap frames per seam: {_overlapFrames} (multi-frame start={UsesMultiFrameStart(continuationModel)})");

        WGNodeData currentMedia = _generator.CurrentMedia;
        Logs.Info($"[VideoConcat] CurrentMedia: DataType={currentMedia.DataType}, Frames={currentMedia.Frames}, Width={currentMedia.Width}, Height={currentMedia.Height}, Path={currentMedia.Path?[0] ?? "null"}");
        
        if (currentMedia.Frames == null || currentMedia.Frames < 1)
        {
            Logs.Warning("[VideoConcat] CurrentMedia has no video frames - VideoConcat should run after video generation");
            return;
        }

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

        JToken widthArr = GetWidthNode();
        JToken heightArr = GetHeightNode();
        // With a Refiner latent-model upscale the first section was generated at the base resolution and then upscaled +
        // refined, so continuations do the same instead of running all their steps at the upscaled resolution.
        ContinuationRefineInfo refine = ResolveContinuationRefine(continuationModel, isText2Video);
        if (refine is not null)
        {
            widthArr = refine.BaseWidth;
            heightArr = refine.BaseHeight;
            Logs.Info($"[VideoConcat] Refiner latent upscale on continuations: base {refine.BaseWidth}x{refine.BaseHeight} -> {refine.Width}x{refine.Height}, refine steps {refine.StartStep}-{refine.Steps}");
        }

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
        
        // FPS can be a node reference (eg. loaded/extended video), so never cast it directly.
        int? videoFps = baseFps ?? currentMedia.GetRawFPS();

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

            if (_enableAutoCaption)
            {
                JArray lastFrame = ExtractLastSingleFrame(previousVideo.Path);

                int preId = _generator.LastID;

                WGNodeData newVideo = GenerateContinuationSection(
                    continuationModel, previousVideo, frames, videoFps ?? 24, sectionSteps, sectionCfg,
                    width, height, widthArr, heightArr, prompt, negPrompt, sectionSeed, i, refine
                );

                // Find CLIPTextEncode's clip and LTXVConditioning's positive
                JArray clipRef = null;
                string ltxvCondId = null;

                for (int nid = preId; nid < _generator.LastID; nid++)
                {
                    string nidStr = nid.ToString();
                    if (!_generator.Workflow.TryGetValue(nidStr, out JToken tok) || tok is not JObject n)
                        continue;

                    string ctype = n["class_type"]?.ToString();
                    if (ctype == "CLIPTextEncode" && n["inputs"] is JObject cinp && cinp.TryGetValue("clip", out JToken c))
                        clipRef = c as JArray;
                    if (ctype == "LTXVConditioning" && n["inputs"] is JObject linp && linp.TryGetValue("positive", out JToken _))
                        ltxvCondId = nidStr;
                }

                if (clipRef != null && ltxvCondId != null)
                {
                    JObject captionInputs = new()
                    {
                        ["image"] = lastFrame,
                        ["prompt"] = prompt,
                        ["clip"] = clipRef,
                        ["backend"] = _captionModel.Backend,
                        ["server_url"] = _captionModel.ServerUrl,
                        ["model"] = _captionModel.Model,
                        ["max_tokens"] = 256,
                    };
                    if (!string.IsNullOrWhiteSpace(_captionInstruction))
                    {
                        captionInputs["instruction"] = _captionInstruction;
                    }
                    string captionNode = _generator.CreateNode("VideoAutoCaption", captionInputs);
                    JArray captionRef = new JArray(captionNode, 0);

                    (_generator.Workflow[ltxvCondId]["inputs"] as JObject)["positive"] = captionRef;
                    Logs.Info($"[VideoConcat] AutoCaption wired: {captionNode} → LTXVConditioning {ltxvCondId}");
                }
                else
                {
                    Logs.Warning($"[VideoConcat] AutoCaption: clip={clipRef != null} ltxvcond={ltxvCondId != null}");
                }

                Logs.Info($"[VideoConcat] Section {i} generated with auto-caption: Frames={newVideo.Frames}");
                videoChunks.Add(newVideo);
                
                if (newVideo.AttachedAudio != null)
                {
                    WGNodeData currentAudioVae = _generator.CurrentAudioVae ?? audioVae;
                    WGNodeData sectionAudio = newVideo.AttachedAudio;
                    if (sectionAudio.DataType == WGNodeData.DT_LATENT_AUDIO && currentAudioVae != null)
                        sectionAudio = sectionAudio.DecodeLatents(currentAudioVae, true);
                    if (sectionAudio != null && sectionAudio.DataType == WGNodeData.DT_AUDIO)
                        audioChunks.Add(sectionAudio.Path);
                }
                
                previousVideo = newVideo;
            }
            else
            {
                Logs.Info($"[VideoConcat] Section {i}: frames={frames}, prompt='{prompt.Substring(0, Math.Min(50, prompt.Length))}...', steps={sectionSteps}, seed={sectionSeed}");

                WGNodeData newVideo = GenerateContinuationSection(
                    continuationModel, previousVideo, frames, videoFps ?? 24, sectionSteps, sectionCfg,
                    width, height, widthArr, heightArr, prompt, negPrompt, sectionSeed, i, refine
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
        }

        if (_enableColorMatch && videoChunks.Count > 1)
        {
            // The first '_overlapFrames' of each section re-create the last frames of the previous one, so their color
            // difference is pure drift. Correct the whole section with that constant offset (it used to decay back to the
            // section's own colors right after the seam, which made the color jump visible a few frames later).
            for (int i = 1; i < videoChunks.Count; i++)
            {
                WGNodeData previousChunk = videoChunks[i - 1];
                WGNodeData currentChunk = videoChunks[i];
                JArray refFrames = ExtractLastFrames(previousChunk.Path, _overlapFrames);
                JArray colorMatched = ApplyColorMatching(currentChunk.Path, refFrames, _colorStrength, _overlapFrames);
                videoChunks[i] = currentChunk.WithPath(colorMatched);
            }
        }

        JArray concatenatedVideo = ConcatenateVideoChunks(videoChunks, out int totalFrames);

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
        result.FPS = videoFps.HasValue ? videoFps.Value : currentMedia.FPS;

        if (audioChunks.Count > 0)
        {
            JArray concatenatedAudio = ConcatenateAudioChunks(audioChunks, videoFps ?? 24);
            if (concatenatedAudio != null)
            {
                result.AttachedAudio = new WGNodeData(concatenatedAudio, _generator, WGNodeData.DT_AUDIO, _generator.CurrentCompat());
            }
        }
        
        // Saved by the core True Final Save step (priority 100) like any other video: honors 'Video Format'
        // and avoids a duplicate output (this step used to save it itself, then the final save saved it again).
        _generator.CurrentMedia = result;
        Logs.Info($"[VideoConcat] Concatenated {_sections.Count} sections ({totalFrames} frames), left for the final save");

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
        JToken widthArr,
        JToken heightArr,
        string prompt,
        string negPrompt,
        long seed,
        int sectionIndex,
        ContinuationRefineInfo refine)
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
            ["b"] = -_overlapFrames
        });
        JArray fromEndCount = [fromEndCountNode, 0];

        string partialBatchNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = previousVideo.Path,
            ["batch_index"] = fromEndCount,
            ["length"] = _overlapFrames
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
            BatchLen = _overlapFrames,
            ContextID = T2IParamInput.SectionID_Video
        };

        // For LTXV1 models only, replace the standard VAE decode node with LTXVTiledVAEDecode.
        // LTXV2 already uses VAEDecodeTiled via SwarmUI's built-in DecodeLatents (IsLTXV2()),
        // so we must NOT replace it — doing so causes black output with corrupted timestamps.
        string compatClass = videoModel?.ModelClass?.CompatClass?.ID ?? "";
        bool isLTXV1 = compatClass == "lightricks-ltx-video";

        try
        {
            _generator.CreateImageToVideo(genInfo);
        }
        finally
        {
        }

        WGNodeData result = _generator.CurrentMedia;

        if (refine is not null)
        {
            result = ApplyContinuationRefine(genInfo, result, partialBatch, refine, seed + 1, sectionIndex);
        }

        if (isLTXV1 && result.DataType == WGNodeData.DT_VIDEO && genInfo.Vae != null)
        {
            string decodeNodeId = result.Path[0]?.ToString();

            if (!string.IsNullOrEmpty(decodeNodeId))
            {
                JObject decodeNode = _generator.Workflow[decodeNodeId] as JObject;

                if (decodeNode != null)
                {
                    string nodeType = decodeNode["class_type"]?.ToString() ?? "";
                    JToken samplesInput = decodeNode["inputs"]?["samples"] ?? decodeNode["inputs"]?["latent"];

                    if (samplesInput != null && nodeType != "VAEDecodeTiled")
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
                    else if (nodeType == "VAEDecodeTiled")
                    {
                        Logs.Info($"[VideoConcat] Section {sectionIndex}: SwarmUI already uses VAEDecodeTiled, keeping it (nodeId={decodeNodeId})");
                    }
                    else
                    {
                        Logs.Warning($"[VideoConcat] Could not find samples/latents input in decode node (type={nodeType})");
                    }
                }
                else
                {
                    Logs.Warning($"[VideoConcat] Decode node '{decodeNodeId}' not found in Workflow");
                }
            }
        }

        // Ensure we have the correct frame count metadata
        result.Frames = frames;
        result.FPS = fps;

        Logs.Info($"[VideoConcat] Section {sectionIndex}: requested {frames} frames, result type={result.DataType}");

        return result;
    }

    /// <summary>Settings of the core Refiner's latent-model upscale, mirrored on each continuation section.</summary>
    public record ContinuationRefineInfo(string UpscaleModel, int BaseWidth, int BaseHeight, int Width, int Height, int Steps, int StartStep, double Cfg, bool AddNoise);

    /// <summary>Returns the Refiner latent-model upscale settings to mirror on continuations, or null when the first section was not refined that way.</summary>
    private ContinuationRefineInfo ResolveContinuationRefine(T2IModel continuationModel, bool isText2Video)
    {
        T2IParamInput input = _generator.UserInput;
        // In Image2Video mode the refiner runs on the init image, not on the video, so the first section is not upscaled.
        if (!isText2Video || !input.TryGet(T2IParamTypes.RefinerMethod, out string method) || !input.TryGet(T2IParamTypes.RefinerControl, out double refinerControl))
        {
            return null;
        }
        if (!input.TryGet(T2IParamTypes.RefinerUpscale, out double refineUpscale) || refineUpscale == 1)
        {
            return null;
        }
        string upscaleMethod = input.Get(ComfyUIBackendExtension.RefinerUpscaleMethod, "None");
        if (!upscaleMethod.StartsWith("latentmodel-"))
        {
            return null;
        }
        string compatId = continuationModel?.ModelClass?.CompatClass?.ID ?? "";
        if (compatId != T2IModelClassSorter.CompatMiniMaxH3.ID && compatId != T2IModelClassSorter.CompatLtxv2.ID && compatId != T2IModelClassSorter.CompatHunyuanVideo1_5.ID)
        {
            Logs.Warning($"[VideoConcat] Refiner latent-model upscale is not supported on continuations for '{compatId}', continuations run at the upscaled resolution");
            return null;
        }
        T2IModel mainModel = input.Get(T2IParamTypes.Model, null);
        if (mainModel?.ModelClass?.CompatClass?.ID != compatId)
        {
            Logs.Warning($"[VideoConcat] Extension Model class '{compatId}' differs from the main model, the Refiner latent upscale model cannot be reused, continuations run at the upscaled resolution");
            return null;
        }
        int baseWidth = input.GetImageWidth();
        int baseHeight = input.GetImageHeight();
        // Same target size as the core Refiner step.
        int width = ((int)Math.Round(baseWidth * refineUpscale) / 16) * 16;
        int height = ((int)Math.Round(baseHeight * refineUpscale) / 16) * 16;
        if (compatId == T2IModelClassSorter.CompatMiniMaxH3.ID)
        {
            width = (width / 32) * 32;
            height = (height / 32) * 32;
        }
        int steps = input.Get(T2IParamTypes.RefinerSteps, input.Get(T2IParamTypes.Steps, 20, sectionId: T2IParamInput.SectionID_Refiner), sectionId: T2IParamInput.SectionID_Refiner);
        int startStep = refinerControl <= 0 ? steps : (int)Math.Round(steps * (1 - refinerControl));
        double cfg = input.Get(T2IParamTypes.RefinerCFGScale, input.Get(T2IParamTypes.CFGScale, 7, sectionId: T2IParamInput.SectionID_Refiner), sectionId: T2IParamInput.SectionID_Refiner);
        return new ContinuationRefineInfo(upscaleMethod.After("latentmodel-"), baseWidth, baseHeight, width, height, steps, startStep, cfg, method != "StepSwapNoisy");
    }

    /// <summary>Latent-upscales a base-resolution continuation section and refines it, like the core Refiner step does for the first section.</summary>
    private WGNodeData ApplyContinuationRefine(WorkflowGenerator.ImageToVideoGenInfo genInfo, WGNodeData decoded, JArray sourceFrames, ContinuationRefineInfo refine, long seed, int sectionIndex)
    {
        WGNodeData vae = genInfo.Vae;
        WGNodeData audioVae = _generator.CurrentAudioVae;
        string compatId = genInfo.VideoModel?.ModelClass?.CompatClass?.ID ?? "";
        // The decoded section comes straight from a VAE decode, so this resolves back to the sampled latent (no re-encode).
        WGNodeData latent = decoded.EncodeToLatent(vae);
        JArray pos = genInfo.PosCond, neg = genInfo.NegCond;
        // Previous section's frame at full resolution, to re-anchor the refine pass.
        string firstFrameNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = sourceFrames,
            ["batch_index"] = 0,
            ["length"] = 1
        });
        JArray firstFrame = [firstFrameNode, 0];
        string upscaleLoader = _generator.CreateNode("LatentUpscaleModelLoader", new JObject()
        {
            ["model_name"] = refine.UpscaleModel
        });
        JArray upscaleModel = [upscaleLoader, 0];
        if (compatId == T2IModelClassSorter.CompatMiniMaxH3.ID)
        {
            // Rejoin the section's audio first, like the core Refiner (the upscale node passes audio through unchanged).
            latent = latent.AsSamplingLatent(vae, audioVae);
            string upscaled = _generator.CreateNode("MiniMaxH3LatentUpscaleWithModel", new JObject()
            {
                ["model"] = upscaleModel,
                ["samples"] = latent.Path,
                ["width"] = refine.Width,
                ["height"] = refine.Height
            });
            latent = latent.WithPath([upscaled, 0]);
            // The section's keyframe latent was encoded at the base resolution, replace it with one matching the upscaled latent.
            string keyframes = _generator.CreateNode("SwarmMiniMaxH3AddKeyframes", new JObject()
            {
                ["vae"] = vae.Path,
                ["latent"] = latent.Path,
                ["conditioning"] = pos,
                ["first_frame"] = firstFrame
            });
            pos = [keyframes, 0];
        }
        else if (compatId == T2IModelClassSorter.CompatLtxv2.ID)
        {
            latent = latent.AsLatentImage(vae);
            string cropGuides = _generator.CreateNode("LTXVCropGuides", new JObject()
            {
                ["positive"] = pos,
                ["negative"] = neg,
                ["latent"] = latent.Path
            });
            pos = [cropGuides, 0];
            neg = [cropGuides, 1];
            string upscaled = _generator.CreateNode("LTXVLatentUpsampler", new JObject()
            {
                ["vae"] = vae.Path,
                ["samples"] = new JArray(cropGuides, 2),
                ["upscale_model"] = upscaleModel
            });
            string preproc = _generator.CreateNode("LTXVPreprocess", new JObject()
            {
                ["image"] = firstFrame,
                ["img_compression"] = 18
            });
            string anchored = _generator.CreateNode("LTXVImgToVideoInplace", new JObject()
            {
                ["vae"] = vae.Path,
                ["image"] = new JArray(preproc, 0),
                ["latent"] = new JArray(upscaled, 0),
                ["strength"] = 1.0,
                ["bypass"] = false
            });
            latent = latent.WithPath([anchored, 0], WGNodeData.DT_LATENT_VIDEO);
        }
        else
        {
            latent = latent.AsLatentImage(vae);
            string upscaled = _generator.CreateNode("HunyuanVideo15LatentUpscaleWithModel", new JObject()
            {
                ["model"] = upscaleModel,
                ["samples"] = latent.Path,
                ["upscale_method"] = "bilinear",
                ["width"] = refine.Width,
                ["height"] = refine.Height,
                ["crop"] = "disabled"
            });
            latent = latent.WithPath([upscaled, 0], WGNodeData.DT_LATENT_VIDEO);
            // The I2V conditioning carries the start image latent at the base resolution, rebuild it at the upscaled one.
            if (_generator.Workflow[$"{pos[0]}"] is JObject i2vNode && $"{i2vNode["class_type"]}" == "HunyuanVideo15ImageToVideo")
            {
                JObject i2vInputs = (JObject)i2vNode["inputs"].DeepClone();
                i2vInputs["width"] = refine.Width;
                i2vInputs["height"] = refine.Height;
                i2vInputs["start_image"] = firstFrame;
                string rebuilt = _generator.CreateNode("HunyuanVideo15ImageToVideo", i2vInputs);
                pos = [rebuilt, 0];
                neg = [rebuilt, 1];
            }
            else
            {
                Logs.Warning($"[VideoConcat] Section {sectionIndex}: HunyuanVideo15ImageToVideo conditioning not found, refining with the base resolution conditioning");
            }
        }
        latent.Width = refine.Width;
        latent.Height = refine.Height;
        if (refine.StartStep < refine.Steps)
        {
            latent = latent.AsSamplingLatent(vae, audioVae);
            string explicitSampler = _generator.UserInput.Get(ComfyUIBackendExtension.SamplerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false) ?? _generator.UserInput.Get(ComfyUIBackendExtension.RefinerSamplerParam, null);
            string explicitScheduler = _generator.UserInput.Get(ComfyUIBackendExtension.SchedulerParam, null, sectionId: T2IParamInput.SectionID_Refiner, includeBase: false) ?? _generator.UserInput.Get(ComfyUIBackendExtension.RefinerSchedulerParam, null);
            string sampled = _generator.CreateKSampler(genInfo.Model.Path, pos, neg, latent.Path, refine.Cfg, refine.Steps, refine.StartStep, 10000,
                seed, false, refine.AddNoise, doTiled: _generator.UserInput.Get(T2IParamTypes.RefinerDoTiling, false),
                explicitSampler: explicitSampler, explicitScheduler: explicitScheduler, sectionId: T2IParamInput.SectionID_Refiner);
            latent = latent.WithPath([sampled, 0]);
        }
        WGNodeData result = latent.AsRawImage(vae);
        result.Width = refine.Width;
        result.Height = refine.Height;
        Logs.Info($"[VideoConcat] Section {sectionIndex}: latent upscaled to {refine.Width}x{refine.Height} and refined ({refine.Steps - refine.StartStep} steps)");
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

    private JArray ExtractLastSingleFrame(JArray video)
    {
        string frameCountNode = _generator.CreateNode("SwarmCountFrames", new JObject()
        {
            ["image"] = video
        });

        string lastIndexNode = _generator.CreateNode("SwarmIntAdd", new JObject()
        {
            ["a"] = new JArray(frameCountNode, 0),
            ["b"] = -1
        });

        string extractNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = video,
            ["batch_index"] = new JArray(lastIndexNode, 0),
            ["length"] = 1
        });

        return [extractNode, 0];
    }

    private JArray ApplyColorMatching(JArray currentVideo, JArray referenceVideo, double strength, int sourceFrames)
    {
        string colorMatchNode = _generator.CreateNode("VideoColorMatch", new JObject()
        {
            ["video"] = currentVideo,
            ["reference"] = referenceVideo,
            ["strength"] = strength,
            ["blend_frames"] = 0,
            ["mode"] = "constant",
            ["source_frames"] = sourceFrames
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

            // Smoothing is done at the seam inside the crossfade node, where the real frame index is known
            // (a separate VideoTemporalBlend on the joined video only reached its very start and end).
            string crossfadeNode = _generator.CreateNode("VideoCrossFadeTransition", new JObject()
            {
                ["video_a"] = currentVideo,
                ["video_b"] = nextVideo,
                ["transition_frames"] = _overlapFrames,
                ["blend_mode"] = _transitionMode,
                ["include_overlap"] = _frameMode == "include_overlap",
                ["seam_smooth_strength"] = _enableTemporalBlend ? _temporalStrength : 0.0,
                ["seam_smooth_frames"] = Math.Max(2, _transitionFrames / 2)
            });

            currentVideo = [crossfadeNode, 0];
            
            if (_frameMode == "include_overlap")
            {
                totalFrames = totalFrames + nextFrames;
            }
            else
            {
                totalFrames = totalFrames + nextFrames - _overlapFrames;
            }
        }

        return currentVideo;
    }

    private JArray ConcatenateAudioChunks(List<JArray> chunks, int fps)
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
        // Each section's audio also covers the overlap frames that the video join drops, so the audio must lose exactly
        // that duration too (trim + crossfade), or it drifts out of sync by a few frames per seam.
        double overlapSeconds = _overlapFrames / (double)fps;
        double crossfadeSeconds = Math.Min(_audioCrossfadeFrames, _overlapFrames) / (double)fps;
        for (int i = 1; i < chunks.Count; i++)
        {
            string crossfadeNode = _generator.CreateNode("AudioCrossFade", new JObject()
            {
                ["audio_a"] = result,
                ["audio_b"] = chunks[i],
                ["crossfade_samples"] = 882,
                ["crossfade_seconds"] = crossfadeSeconds,
                ["trim_b_seconds"] = overlapSeconds - crossfadeSeconds
            });
            result = new JArray(crossfadeNode, 0);
        }
        
        return result;
    }

    /// <summary>True if core's CreateImageToVideo feeds all BatchLen frames as the start (Wan), instead of only the first one.</summary>
    public static bool UsesMultiFrameStart(T2IModel model)
    {
        string compatClass = model?.ModelClass?.CompatClass?.ID ?? "";
        return compatClass == "wan-21-14b" || compatClass == "wan-21-1_3b" || compatClass == "wan-22-5b";
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