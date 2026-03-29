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
    private bool _enableColorMatch = true;
    private double _colorStrength = 0.5;
    private bool _enableTemporalBlend = true;
    private double _temporalStrength = 0.5;

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

        WGNodeData currentMedia = _generator.CurrentMedia;
        if (currentMedia.Frames == null || currentMedia.Frames < 1)
        {
            Logs.Warning("[VideoConcat] CurrentMedia has no video frames - VideoConcat should run after Image To Video step");
            return;
        }

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

        List<JArray> videoChunks = [currentMedia.Path];
        int? videoFps = baseFps ?? currentMedia.FPS;

        for (int i = 1; i < _sections.Count; i++)
        {
            JObject section = _sections[i] as JObject;
            int frames = section["duration_frames"]?.Value<int>() ?? baseFrames ?? 25;
            string prompt = i < _sectionPrompts.Length && !string.IsNullOrEmpty(_sectionPrompts[i]) 
                ? _sectionPrompts[i] 
                : _generator.UserInput.Get(T2IParamTypes.Prompt, "");
            long sectionSeed = baseSeed + i + 1000;
            double? sectionCfg = baseCfg;
            int sectionSteps = baseSteps;

            currentMedia = GenerateContinuationSection(
                videoModel, currentMedia, frames, videoFps ?? 24, sectionSteps, sectionCfg,
                width, height, widthArr, heightArr, prompt, negPrompt, sectionSeed, i
            );

            if (_enableColorMatch && videoChunks.Count > 0)
            {
                JArray colorMatched = ApplyColorMatching(currentMedia.Path, videoChunks[^1], _colorStrength);
                currentMedia = currentMedia.WithPath(colorMatched);
            }

            videoChunks.Add(currentMedia.Path);
        }

        JArray concatenatedPath = ConcatenateVideoChunks(videoChunks);

        if (_enableTemporalBlend)
        {
            concatenatedPath = ApplyTemporalBlend(concatenatedPath, _temporalStrength);
        }

        _generator.CurrentMedia = currentMedia.WithPath(concatenatedPath);
        _generator.CurrentMedia.FPS = videoFps;
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

        _generator.CurrentMedia = previousVideo.WithPath(partialBatch);
        _generator.CreateImageToVideo(genInfo);
        
        WGNodeData result = _generator.CurrentMedia.AsRawImage(genInfo.Vae);

        string cutNode = _generator.CreateNode("ImageFromBatch", new JObject()
        {
            ["image"] = result.Path,
            ["batch_index"] = _transitionFrames,
            ["length"] = frames - _transitionFrames
        });

        return result.WithPath([cutNode, 0]);
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

    private JArray ConcatenateVideoChunks(List<JArray> chunks)
    {
        if (chunks.Count == 0)
            return null;
        
        if (chunks.Count == 1)
            return chunks[0];

        JArray currentBatch = chunks[0];

        for (int i = 1; i < chunks.Count; i++)
        {
            string transitionNode = _generator.CreateNode("ImageBatch", new JObject()
            {
                ["image1"] = currentBatch,
                ["image2"] = chunks[i]
            });
            currentBatch = [transitionNode, 0];
        }

        return currentBatch;
    }

    private JArray ApplyTemporalBlend(JArray video, double strength)
    {
        string blendNode = _generator.CreateNode("VideoTemporalBlend", new JObject()
        {
            ["video"] = video,
            ["blend_strength"] = strength,
            ["blend_frames"] = _transitionFrames
        });
        return [blendNode, 0];
    }
}