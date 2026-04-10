# VideoConcat Extension - Estado Actual

**Última actualización:** 31 de marzo de 2026

## Goal

Extensión para SwarmUI que permite concatenar múltiples secciones de video con coherencia temporal. Cada sección continúa desde los últimos frames de la sección anterior, soportando diferentes prompts por sección, crossfade transitions, color matching y temporal blending.

## Instrucciones

- La extensión aparece en la UI como grupo "Video Concatenation" con toggle
- Se habilita cuando "Section Prompts" tiene contenido separado por `|||`
- La extensión debe:
  1. Correr DESPUÉS del paso de generación de video (prioridad 11.5)
  2. Tomar el primer video (de Text2Video o Image To Video) como input
  3. Generar videos de continuación para cada prompt usando un modelo Image2Video
  4. Aplicar crossfade transitions entre videos
  5. Aplicar color matching y temporal blending
  6. Concatenar audio con crossfade

## Modos de Operación

### Image2Video (comportamiento original)
- Seleccionar modelo en "Video Model" (Image2Video)
- El primer video se genera con Image To Video
- Las continuaciones usan el mismo modelo

### Text2Video (nuevo)
- Seleccionar modelo Text2Video como modelo principal (Mochi, LTXV, HunyuanVideo, Wan, Cosmos)
- El primer video se genera directamente desde texto
- **Requiere** seleccionar un modelo Image2Video en "Extension Model"
- Las continuaciones usan el Extension Model

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
8. ✅ RTX Video Super Resolution node integration (2x upscale)
9. ✅ VideoFastSave node with NVENC GPU acceleration

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
| `Extension Model` | T2IModel | "" | Modelo Image2Video para continuar secciones (requerido para Text2Video) |
| `Enable RTX Upscale` | bool | false | Aplicar upscaling 2x RTX VSR (computacionalmente costoso) |
| `Enable Fast Save` | bool | true | Usar encoding GPU-accelerado (NVENC) para salida rápida |

## Flujo de Trabajo Actualizado

1. **Prioridad 11:** Image To Video genera Video 1
2. **Prioridad 11.5:** VideoConcat:
   - Para cada sección adicional:
     - Extrae últimos N frames del video anterior
     - Genera nuevo video continuando desde esos frames
     - Aplica color matching con frames de referencia
   - Concatena todos con `VideoCrossFadeTransition`
   - Aplica temporal blending en transiciones
   - Concatena audio con `AudioCrossFade`
   - **Aplica RTX Video Super Resolution** (2x upscale calidad ULTRA, opcional)
   - **Guarda con VideoFastSave** (NVENC GPU acceleration, fallback a CPU optimized)
     - Use NVENC h264_nvenc/hevc_nvenc when available
     - Fallback to libx264/libx265 with veryfast preset when NVENC unavailable
     - Audio preserved properly with aac encoding

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
6. `3272147` - feat: add optional RTX Video Super Resolution upscaling
7. `pending` - feat: add VideoFastSave with NVENC GPU acceleration
8. **Pending commit** - fix: VideoFastSave grey colors + duration 0 (rewrite to use imageio_ffmpeg, add NVENC fallback)

## Auto-ajustes por Modelo

Los ajustes se aplican automáticamente según el modelo detectado (`videoModel.ModelClass.CompatClass.ID`).

### Transition Frames
- **Wan (2.1/2.2)**: Requiere frames en formato 4n+1 (5, 9, 13, 17...). Se auto-ajusta al valor más cercano.
- **LTXV**: Sin cambios (ya funciona correctamente).
- Ejemplo: Si el usuario entra 12 frames para Wan, se auto-ajusta a 13.

### Color Matching (solo Wan)
- **Strength**: Multiplicado x1.4 (ej: 0.5 → 0.7) para compensar el baseline gris diferente
- **Reference Frames**: Aumentados x1.5 (ej: 13 → 19 frames) para mejor referencia de color
- **LTXV**: Mantiene los valores originales del usuario

**Motivo:** Wan genera desde un baseline gris (0.5) mientras LTXV usa `LTXVPreprocess` (CRF compression) que suaviza colores antes del encode.

## Bug Fixes

### VideoFastSave: Video gris + duración 0 (v2.2.0)

**Problema:** El video resultante con "Enable Fast Save" salía gris y sin duración.

**Causas raíz:**
1. Usaba `ffmpeg` del sistema (podía no tener codecs o ser incompatible)
2. Faltaba flag `-n` para sobreescribir sin preguntar (causa hangs)
3. NVENC no tenía flags de color space (`-color_range pc -colorspace bt709 -color_primaries bt709 -color_trc bt709`)
4. Usaba `tempfile.NamedTemporaryFile(delete=False)` en vez de `folder_paths` de ComfyUI

**Soluciones:**
- Usa `imageio_ffmpeg.get_ffmpeg_exe()` (mismo ffmpeg que SwarmSaveAnimationWS)
- Añadido flag `-n` para sobreescribir sin prompt
- Añadidas flags de color space para NVENC (full range BT.709)
- Usa `folder_paths.get_save_image_path()` para directorio temporal (como SwarmSaveAnimationWS)
- Fallback automático CPU si NVENC falla