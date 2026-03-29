# VideoConcat Extension - Estado Actual

**Última actualización:** 29 de marzo de 2026

## Goal

Extensión para SwarmUI que permite concatenar múltiples secciones de video con coherencia temporal. Cada sección continúa desde los últimos frames de la sección anterior, soportando diferentes prompts por sección, color matching y temporal blending.

## Instrucciones

- La extensión aparece en la UI como grupo colapsable "Video Concatenation" con `Toggles: true`
- El grupo se habilita automáticamente cuando "Section Prompts" tiene contenido
- La extensión debe:
  1. Correr DESPUÉS del paso Image To Video (prioridad 11.5)
  2. Tomar el primer video de Image To Video como input (generado con el prompt principal)
  3. Generar videos de continuación para cada prompt en Section Prompts
  4. Concatenar todos los videos juntos
  5. Manejar audio correctamente (de LTXV y modelos similares)
  6. Aplicar color matching y temporal blending

## Flujo de Prompts

El flujo CORRECTO de prompts es:
1. **Video 1**: Se genera con Image To Video usando el prompt principal de SwarmUI
2. **Videos 2+**: Se generan con los prompts de "Section Prompts", separados por `|||`
   - `sectionPrompts[0]` → Video 2
   - `sectionPrompts[1]` → Video 3
   - etc.

Ejemplo con `Section Prompts = "A cat running|||A cat jumping"`:
- Video 1: prompt principal de Image To Video
- Video 2: "A cat running"
- Video 3: "A cat jumping"

## Arquitectura

```
src/Extensions/VideoConcat/
├── VideoConcatExtension.cs      # Registro de extensión, parámetros y workflow step
├── VideoConcatExtension.csproj  # Archivo de proyecto .NET 8
├── VideoConcatenator.cs         # Lógica core de generación y concatenación
├── README.md                    # Documentación de uso
├── STATE.md                     # Este archivo
├── assets/
│   ├── video-concat.js          # Frontend UI (simplificado)
│   └── video-concat.css         # Estilos
└── comfy_node/
    ├── __init__.py
    └── video_concat_nodes.py    # Nodos ComfyUI personalizados
```

## Completado ✅

1. ✅ Extensión compila y registra parámetros
2. ✅ Workflow step corre con prioridad correcta (11.5)
3. ✅ Generación de video para secciones de continuación funciona
4. ✅ Concatenación de video con ImageBatch funciona
5. ✅ Nodos de color matching y temporal blend creados
6. ✅ Repositorio git inicializado con commits
7. ✅ Lógica de prompts corregida (primer prompt = video 2, no video 1)
8. ✅ Grupo con `Toggles: true` - se habilita cuando Section Prompts tiene contenido

## Cómo Funciona el Toggle

El patrón es el mismo que otras extensiones (`Film Grain`, `ReActor`, etc.):
- Grupo con `Toggles: true`
- Toggle aparece junto al nombre del grupo
- Cuando está activado, los parámetros del grupo se envían
- El workflow step solo se ejecuta si `sectionPrompts.Length > 0`

## Parámetros

| Parámetro | Tipo | Default | Descripción |
|-----------|------|---------|-------------|
| `Section Prompts` | string | "" | Prompts para videos 2+, separados por `\|\|\|`. Habilita la extensión. |
| `Section Durations` | string | "" | Frames por sección (incluye video 1) |
| `Transition Frames` | int | 12 | Overlap entre secciones |
| `Enable Color Matching` | bool | true | Match de color entre secciones |
| `Color Match Strength` | double | 0.5 | Intensidad del color matching (0-1) |
| `Enable Temporal Blending` | bool | true | Blending temporal |
| `Temporal Blend Strength` | double | 0.5 | Intensidad del blending (0-1) |

## En Progreso ❌

- ❌ ComfyUI workflow muestra error "Failed to save workflow draft"

## Audio Handling

El audio ahora se maneja correctamente para todos los videos:
1. Cada video generado tiene `AttachedAudio` tipo `DT_LATENT_AUDIO`
2. En `GenerateContinuationSection`, se llama `AsRawImage` que separa video y audio latent
3. En el bucle principal, cada audio latent se decodifica con `LTXVAudioVAEDecode`
4. Todos los audios decodificados se concatenan con `AudioConcat`
5. El audio concatenado final se adjunta al video resultante

**Flujo de audio:**
```
Video 1 (Image To Video) → AttachedAudio (DT_LATENT_AUDIO) → decode → audioChunks[0]
Video 2..N → AttachedAudio (DT_LATENT_AUDIO) → decode → audioChunks[1..N]
audioChunks → AudioConcat → result.AttachedAudio (DT_AUDIO)
result → SaveOutput → final video + audio
```

## Nodos ComfyUI Incluidos

En `comfy_node/video_concat_nodes.py`:
- `VideoColorMatch` - Matching de histograma de color entre videos
- `VideoTemporalBlend` - Suavizado temporal para reducir flickering
- `VideoCrossFadeTransition` - Transiciones crossfade entre videos
- `VideoBatch` - Batching de frames
- `EmptyLatentVideo` - Latent vacío para generación

## Flujo de Trabajo

1. **Prioridad 11:** Image To Video genera el primer video con el prompt principal (nativo SwarmUI)
2. **Prioridad 11.5:** VideoConcat (si Section Prompts tiene contenido):
   - Para cada prompt en Section Prompts (video 2+):
     - Extrae últimos N frames del video anterior
     - Genera nuevo video con el prompt de la sección
     - Aplica color matching con la sección anterior
     - Añade al array de chunks
   - Concatena todos los chunks con `ImageBatch`
   - Aplica temporal blending final
   - Guarda el resultado

## Notas Técnicas

- Usa `ImageToVideoGenInfo` y `CreateImageToVideo()` del sistema existente
- Similar a Video Extend (`<extend:N>`) pero con prompts variables
- Cada sección re-renderiza con el modelo de video seleccionado
- El overlap es crítico para coherencia temporal (frames compartidos)

## Contact/Author

Creado por SwarmUI Extension Generator. Para continuar desarrollo, revisar:
- `WorkflowGeneratorSteps.cs` líneas 1874-2096 (Image To Video y Video Extend)
- `WorkflowGenerator.cs` para nodos y helper methods
- `T2IParamTypes.cs` para parámetros de video existentes
- `WGNodeData.cs` para audio handling