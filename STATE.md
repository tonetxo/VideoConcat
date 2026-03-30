# VideoConcat Extension - Estado Actual

**Última actualización:** 30 de marzo de 2026

## Goal

Extensión para SwarmUI que permite concatenar múltiples secciones de video con coherencia temporal. Cada sección continúa desde los últimos frames de la sección anterior, soportando diferentes prompts por sección, crossfade transitions, color matching y temporal blending.

## Instrucciones

- La extensión aparece en la UI como grupo "Video Concatenation" con toggle
- Se habilita cuando "Section Prompts" tiene contenido separado por `|||`
- La extensión debe:
  1. Correr DESPUÉS del paso Image To Video (prioridad 11.5)
  2. Tomar el primer video de Image To Video como input
  3. Generar videos de continuación para cada prompt
  4. Aplicar crossfade transitions entre videos
  5. Aplicar color matching y temporal blending
  6. Concatenar audio con crossfade

## Flujo de Prompts

1. **Video 1**: Se genera con Image To Video usando el prompt principal
2. **Videos 2+**: Se generan con los prompts de "Section Prompts", separados por `|||`
   - `sectionPrompts[0]` → Video 2
   - `sectionPrompts[1]` → Video 3

Ejemplo: `Section Prompts = "A cat running|||A cat jumping"`
- Video 1: prompt principal de Image To Video
- Video 2: "A cat running"
- Video 3: "A cat jumping"

## Arquitectura

```
src/Extensions/VideoConcat/
├── VideoConcatExtension.cs      # Registro de extensión y parámetros
├── VideoConcatenator.cs         # Lógica core de concatenación
├── README.md                   # Documentación
├── STATE.md                    # Este archivo
└── comfy_node/
    └── video_concat_nodes.py   # Nodos ComfyUI personalizados
```

## Completado ✅

1. ✅ Toggle del grupo funcional
2. ✅ Lógica de prompts corregida
3. ✅ Crossfade transitions implementadas
4. ✅ Color matching usando frames de transición como referencia
5. ✅ Temporal blending solo en zonas de transición
6. ✅ Audio crossfade con overlap blending (no fade in/out)
7. ✅ Modos configurables de transición

## Parámetros

| Parámetro | Tipo | Default | Descripción |
|-----------|------|---------|-------------|
| `Section Prompts` | string | "" | Prompts para videos 2+, separados por `\|\|\|` |
| `Section Durations` | string | "" | Frames por sección (incluye video 1) |
| `Transition Frames` | int | 12 | Frames para crossfade entre secciones |
| `Transition Mode` | string | "crossfade" | crossfade, fade_to_black, fade_to_white, dissolve |
| `Frame Mode` | string | "exclude_overlap" | include_overlap (video + largo), exclude_overlap (largo combinado) |
| `Enable Color Matching` | bool | true | Match de color entre secciones |
| `Color Match Strength` | double | 0.5 | Intensidad del color matching (0-1) |
| `Enable Temporal Blending` | bool | true | Blending temporal en transiciones |
| `Temporal Blend Strength` | double | 0.5 | Intensidad del blending (0-1) |
| `Enable Audio Fade` | bool | true | Crossfade de audio en transiciones |
| `Audio Crossfade Frames` | int | 8 | Frames de video (convertidos a samples automáticamente) |

## Flujo de Transiciones

### Crossfade (modo por defecto)
```
Video 1: [frame 0...120] (121 frames)
Video 2: [frame 0...120] (121 frames)

Crossfade Process:
1. Video 1[0...108] se mantiene (109 frames sin cambios)
2. Blend zone: Video 1[109...120] blend con Video 2[0...11] (12 frames)
3. Video 2[12...120] se mantiene (109 frames sin cambios)

Resultado (exclude_overlap): 109 + 12 + 109 = 230 frames
Resultado (include_overlap): 121 + 121 = 242 frames (con overlap)
```

### Color Matching
- Solo usa los últimos N frames del video anterior como referencia
- Aplica histogram matching antes de concatenar
- Más preciso que comparar videos completos

### Temporal Blending
- Solo aplica en zonas de transición (primeros y últimos N frames)
- No afecta el contenido central del video
- Reduce flickering sin causar efecto ghost

### Audio Crossfade
- Cada chunk mantiene su audio completo
- Durante el overlap: `audio_a * (1-t) + audio_b * t`
- Convierte frames de video a samples: `frames * (44100 / fps)`
- Mismo comportamiento que crossfade de video

## Nodos ComfyUI

- `VideoColorMatch` - Matching de histograma de color
- `VideoTemporalBlend` - Suavizado temporal en transiciones
- `VideoCrossFadeTransition` - Transiciones crossfade con modos
- `AudioCrossFade` - Crossfade de audio con overlap blending
- `VideoBatch` - Batching de frames

## Flujo de Trabajo

1. **Prioridad 11:** Image To Video genera Video 1
2. **Prioridad 11.5:** VideoConcat:
   - Para cada sección adicional:
     - Extrae últimos N frames del video anterior
     - Genera nuevo video continuando desde esos frames
     - Aplica color matching con frames de referencia
   - Concatena todos con `VideoCrossFadeTransition`
   - Aplica temporal blending en transiciones
   - Concatena audio con `AudioCrossFade`

## Notas Técnicas

- Crossfade elimina cortes bruscos entre videos
- Color matching usa frames cercanos, no videos completos
- Temporal blending solo en boundaries, no en todo el video
- Audio crossfade usa overlap blending, no fade in/out aislados

## Commits

1. `accbbcc1` - Fix audio crossfade: use proper overlap blending like video
2. `58f7cfd` - Implement proper audio handling
3. `8479f05` - Remove redundant Toggleable
4. `3141983` - Fix toggle and prompt logic
5. `ec46789` - Major improvements: crossfade transitions, configurable modes, audio fade