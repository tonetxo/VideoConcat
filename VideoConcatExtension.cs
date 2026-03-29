using System.IO;
using System.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using SwarmUI.Builtin_ComfyUIBackend;
using Newtonsoft.Json.Linq;

namespace VideoConcat;

public class VideoConcatExtension : Extension
{
    public static T2IParamGroup VideoConcatGroup;

    public static T2IRegisteredParam<string> VideoConcatSectionPrompts;
    public static T2IRegisteredParam<int> VideoConcatTransitionFrames;
    public static T2IRegisteredParam<double> VideoConcatColorStrength;
    public static T2IRegisteredParam<double> VideoConcatTemporalStrength;
    public static T2IRegisteredParam<bool> VideoConcatEnableColorMatch;
    public static T2IRegisteredParam<bool> VideoConcatEnableTemporalBlend;
    public static T2IRegisteredParam<string> VideoConcatSectionDurations;

    public override void OnPreInit()
    {
        ScriptFiles.Add("assets/video-concat.js");
        StyleSheetFiles.Add("assets/video-concat.css");
    }

    public override void OnInit()
    {
        Logs.Init("VideoConcat Extension initializing...");

        RegisterParameters();
        RegisterComfyNodes();
        RegisterWorkflowStep();
    }

    private void RegisterComfyNodes()
    {
        string rootPath = string.IsNullOrWhiteSpace(FilePath) ? "src/Extensions/VideoConcat" : FilePath;
        string nodeFolder = Path.GetFullPath(Path.Join(rootPath, "comfy_node"));
        if (!Directory.Exists(nodeFolder))
        {
            return;
        }
        if (ComfyUISelfStartBackend.CustomNodePaths.Contains(nodeFolder))
        {
            return;
        }

        ComfyUISelfStartBackend.CustomNodePaths.Add(nodeFolder);
        Logs.Init($"VideoConcat: added {nodeFolder} to ComfyUI CustomNodePaths");
    }

    private static void RegisterParameters()
    {
        VideoConcatGroup = new(
            Name: "Video Concatenation",
            Description: "Chain multiple video sections with temporal coherence.\n" +
                         "Each section continues from the last frames of the previous section.\n" +
                         "Requires a Video Model selected in 'Image To Video' group.\n" +
                         "Enter prompts separated by '|||' to use (minimum 2 prompts to activate).\n" +
                         "Optional: Set durations per section, or use default Video Frames.",
            Toggles: false,
            Open: false,
            OrderPriority: 8,
            IsAdvanced: false
        );

        double priority = 0;

        VideoConcatSectionDurations = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Section Durations",
            Description: "Duration in frames for each video section, separated by commas.\n" +
                         "Example: '25,30,25' for 3 sections with 25, 30, and 25 frames.\n" +
                         "OPTIONAL: If empty, uses the number of prompts (from '|||' separators) with default Video Frames.\n" +
                         "If fewer durations than prompts, remaining use default Video Frames.",
            Default: "",
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true
        ));

        VideoConcatSectionPrompts = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Section Prompts",
            Description: "Prompts for each video section, separated by '|||'.\n" +
                         "The first prompt is for the first section.\n" +
                         "If empty or fewer prompts than sections, uses the main Prompt.\n" +
                         "Example: 'A cat walking|||A cat running|||A cat jumping'",
            Default: "",
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            ViewType: ParamViewType.PROMPT
        ));

        VideoConcatTransitionFrames = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Transition Frames",
            Description: "Number of frames to use for crossfade transitions between sections.\n" +
                         "More frames = smoother transition but longer generation.\n" +
                         "Recommended: 8-24 frames.",
            Default: "12",
            Min: 1,
            Max: 60,
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true
        ));

        VideoConcatEnableColorMatch = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Enable Color Matching",
            Description: "Match colors between video sections for visual coherence.\n" +
                         "Uses histogram matching to align brightness and color distribution.",
            Default: "true",
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true
        ));

        VideoConcatColorStrength = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "Color Match Strength",
            Description: "How strongly to apply color matching between sections.\n" +
                         "1.0 = full match, 0.0 = no adjustment.",
            Default: "0.5",
            Min: 0,
            Max: 1,
            Step: 0.05,
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true,
            DependNonDefault: VideoConcatEnableColorMatch.Type.ID
        ));

        VideoConcatEnableTemporalBlend = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Enable Temporal Blending",
            Description: "Blend frames temporally to create smooth transitions.\n" +
                         "Reduces flickering and improves motion continuity between sections.",
            Default: "true",
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true
        ));

        VideoConcatTemporalStrength = T2IParamTypes.Register<double>(new T2IParamType(
            Name: "Temporal Blend Strength",
            Description: "How strongly to apply temporal blending.\n" +
                         "1.0 = maximum smoothing, 0.0 = no temporal blending.",
            Default: "0.5",
            Min: 0,
            Max: 1,
            Step: 0.05,
            Group: VideoConcatGroup,
            OrderPriority: priority++,
            FeatureFlag: "video",
            DoNotPreview: true,
            DependNonDefault: VideoConcatEnableTemporalBlend.Type.ID
        ));
    }

    private static void RegisterWorkflowStep()
    {
        WorkflowGenerator.AddStep(g =>
        {
            string durationsRaw = g.UserInput.Get(VideoConcatSectionDurations, "") ?? "";
            string promptsRaw = g.UserInput.Get(VideoConcatSectionPrompts, "") ?? "";
            string[] sectionPrompts = promptsRaw.Split(["|||"], StringSplitOptions.RemoveEmptyEntries);
            
            if (string.IsNullOrWhiteSpace(durationsRaw) && sectionPrompts.Length < 2)
            {
                return;
            }

            if (!g.Features.Contains("video"))
            {
                throw new SwarmUserErrorException("Video concatenation requires video feature support");
            }

            if (!g.UserInput.TryGet(T2IParamTypes.VideoModel, out T2IModel videoModel) || videoModel == null)
            {
                throw new SwarmUserErrorException("Video concatenation requires a Video Model selected in 'Image To Video'");
            }

            int[] durations = ParseDurations(durationsRaw);
            
            if (durations.Length == 0 && sectionPrompts.Length >= 2)
            {
                int defaultFrames = g.UserInput.Get(T2IParamTypes.VideoFrames, 25, sectionId: T2IParamInput.SectionID_Video);
                durations = Enumerable.Repeat(defaultFrames, sectionPrompts.Length).ToArray();
            }
            
            if (durations.Length < 2 && sectionPrompts.Length >= 2)
            {
                int defaultFrames = g.UserInput.Get(T2IParamTypes.VideoFrames, 25, sectionId: T2IParamInput.SectionID_Video);
                int missing = sectionPrompts.Length - durations.Length;
                durations = durations.Concat(Enumerable.Repeat(defaultFrames, missing)).ToArray();
            }
            
            if (durations.Length < 2)
            {
                throw new SwarmUserErrorException("Video concatenation requires at least 2 section durations or prompts (use '|||' to separate prompts)");
            }

            int transitionFrames = g.UserInput.Get(VideoConcatTransitionFrames, 12);

            try
            {
                JArray sections = new JArray();
                foreach (int duration in durations)
                {
                    sections.Add(new JObject { ["duration_frames"] = duration });
                }

                new VideoConcatenator(g)
                    .SetSections(sections, sectionPrompts)
                    .SetTransitionFrames(transitionFrames)
                    .Concatenate();
            }
            catch (Exception ex)
            {
                Logs.Error($"VideoConcat extension error: {ex.Message}");
                throw new SwarmUserErrorException($"Failed to concatenate videos: {ex.Message}");
            }
        }, 11.5);
    }

    private static int[] ParseDurations(string durationsRaw)
    {
        string[] parts = durationsRaw.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        List<int> durations = [];
        foreach (string part in parts)
        {
            if (int.TryParse(part.Trim(), out int duration) && duration > 0)
            {
                durations.Add(duration);
            }
        }
        return durations.ToArray();
    }
}