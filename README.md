# VideoConcat Extension for SwarmUI

Extension for concatenating multiple video sections with temporal and color coherence in SwarmUI.

## Features

- **Video Concatenation**: Join multiple video sections sequentially
- **Coherent Color Matching**: Match colors between video sections for visual consistency
- **Temporal Blending**: Smooth transitions between frames to reduce flickering
- **Per-Section Prompts**: Configure different prompts for each video section (starting from video 2)
- **Configurable Durations**: Set specific frame counts for each section
- **Toggle Switch**: Easily enable/disable the extension from the UI

## Installation

This extension installs in `src/Extensions/VideoConcat/` and will be automatically loaded by SwarmUI.

To build:
```bash
./update-linux.sh  # or update-windows.bat / update-macos.sh
```

## Usage

1. Enable "Enable Video Concatenation" toggle in the "Video Concatenation" parameter group
2. Set up Image To Video with a video model and the main prompt (this generates video 1)
3. Add prompts for subsequent videos using "Section Prompts" separated by `|||`:
   - Example: `A cat running|||A cat jumping` generates:
     - Video 1: uses main prompt from Image To Video
     - Video 2: "A cat running"
     - Video 3: "A cat jumping"
4. Optionally configure durations for all sections (comma-separated)
5. Configure transition settings:
   - **Transition Frames**: Number of frames for crossfade (default: 12)
   - **Enable Color Matching**: Match colors between sections
   - **Color Match Strength**: 0.0-1.0 blend strength
   - **Enable Temporal Blending**: Smooth frame transitions
   - **Temporal Blend Strength**: 0.0-1.0 smoothing intensity

## Parameters

### Enable Video Concatenation
Toggle to enable/disable video concatenation. When enabled, additional videos will be generated and concatenated.

### Section Prompts
Prompts for video sections 2 and beyond, separated by `|||`. 
- Video 1 uses the main prompt from "Image To Video"
- Subsequent videos use the prompts entered here
- Example: `A cat running|||A cat jumping` generates 3 videos total

### Section Durations
Durations in frames for each video section (including the first), separated by commas.
- Example: `25,30,25` for 3 sections
- If empty, uses default Video Frames for all sections

### Transition Settings
- **Transition Frames**: 1-60 frames (default: 12)
- **Color Match Strength**: 0.0-1.0 (default: 0.5)
- **Temporal Blend Strength**: 0.0-1.0 (default: 0.5)

## ComfyUI Nodes

The extension includes custom ComfyUI nodes:

- `VideoColorMatch`: Match histogram between video sections
- `VideoTemporalBlend`: Temporal smoothing for frame coherence
- `VideoCrossFadeTransition`: Create crossfade transitions between videos
- `VideoBatch`: Batch video frames together
- `EmptyLatentVideo`: Create empty latent for video generation

## Architecture

```
src/Extensions/VideoConcat/
├── VideoConcatExtension.cs      # Main extension class
├── VideoConcatExtension.csproj  # Project file
├── VideoConcatenator.cs         # Core concatenation logic
├── assets/
│   ├── video-concat.js          # Frontend UI
│   └── video-concat.css         # Styling
├── comfy_node/
│   ├── __init__.py              # Python init
│   └── video_concat_nodes.py    # ComfyUI nodes
└── README.md
```

## Technical Details

### Workflow Integration
- Registers parameters in `OnPreInit`
- Adds workflow step at priority 10.5 (after video generation, before final save)
- Integrates with existing SwarmUI video model infrastructure

### Color Matching
Uses histogram matching to align brightness and color distribution between sections. The matching is applied with configurable strength to preserve original content.

### Temporal Blending
Applies exponential moving average smoothing between frames to reduce temporal inconsistency and flickering at section boundaries.

## Dependencies

- SwarmUI core
- ComfyUI backend
- PyTorch (for ComfyUI nodes)

## License

MIT License

## Author

Toño Pita
