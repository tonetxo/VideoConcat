# VideoConcat Extension - Estado Actual

**Última actualización:** 29 de marzo de 2026

## Resumen

Extensión para concatenar múltiples secciones de video con coherencia temporal y de color, integrada con el sistema de Image-to-Video de SwarmUI.

## Arquitectura

```
src/Extensions/VideoConcat/
├── VideoConcatExtension.cs      # Parámetros y registro de extensión
├── VideoConcatExtension.csproj  # Archivo de proyecto .NET 8
├── VideoConcatenator.cs         # Lógica de generación y concatenación
├── README.md                    # Documentación de uso
├── STATE.md                     # Este archivo
├── assets/
│   ├── video-concat.js          # Frontend UI (simplificado)
│   └── video-concat.css          # Estilos
└── comfy_node/
    ├── __init__.py
    └── video_concat_nodes.py    # Nodos ComfyUI personalizados
```

## Comportamiento Actual (Encadenado)

1. **Primera sección:** Genera video desde la imagen inicial usando el modelo i2v configurado
2. **Secciones siguientes:**
   - Extrae los últimos N frames de la sección anterior (N = Transition Frames)
   - Genera nuevo video continuando desde esos frames
   - Corta los frames de overlap para evitar repetición
   - Aplica color matching opcional
   - Concatena todo
3. **Final:** Aplica blending temporal si está habilitado

## Parámetros Registrados

| Parámetro | Tipo | Descripción |
|-----------|------|-------------|
| `Section Durations` | string | Frames por sección, separados por coma: "25,30,25". OPCIONAL: si vacío, usa número de prompts con frames por defecto |
| `Section Prompts` | string | Prompts separados por `\|\|\|`: "cat walking\|\|\|cat running". Mínimo 2 para activar |
| `Transition Frames` | int | Frames de overlap entre secciones (default: 12) |
| `Enable Color Matching` | bool | Match de color entre secciones (default: true) |
| `Color Match Strength` | double | Intensidad del color matching 0-1 (default: 0.5) |
| `Enable Temporal Blending` | bool | Blending temporal (default: true) |
| `Temporal Blend Strength` | double | Intensidad del blending 0-1 (default: 0.5) |

## Activación

La extensión se activa cuando:
- Hay **Section Durations** con al menos 2 valores, **O**
- Hay **Section Prompts** con al menos 2 prompts separados por `|||`
- Y hay un **Video Model** seleccionado en "Image To Video"

Si no se especifican duraciones, usa el valor de "Video Frames" (default 25) para todas las secciones.

## Dependencias del Sistema

- Usa parámetros existentes de SwarmUI:
  - `T2IParamTypes.VideoModel` (requerido, del grupo Image To Video)
  - `T2IParamTypes.VideoFrames`, `VideoFPS`, `VideoSteps`, `VideoCFG`
  - `T2IParamTypes.VideoResolution`
  - `T2IParamTypes.Seed`, `Prompt`, `NegativePrompt`

- Workflow step registrado en prioridad **10.5** (después de generación de video)

## Nodos ComfyUI Incluidos

1. **VideoColorMatch** - Matching de histograma de color entre videos
2. **VideoTemporalBlend** - Suavizado temporal para reducir flickering
3. **VideoCrossFadeTransition** - Transiciones crossfade
4. **VideoBatch** - Batching de frames
5. **EmptyLatentVideo** - Latent vacío para generación

## Estado del Build

✅ Compila correctamente

⚠️ Warning esperado: "Extension 'VideoConcatExtension' did not come from git" (normal, no está en repo git)

## Bug Fixed (29/03/2026 - Iteración final)

**Problema:** La extensión no generaba el segundo video.

**Causas:**
1. Workflow priority 10.5 corría ANTES de Image To Video (priority 11) - cambiado a 11.5
2. No se llamaba `SaveOutput` al final
3. Color matching y temporal blend estaban deshabilitados innecesariamente

**Solución final:**
- Priority cambiada a 11.5 (después de Image To Video)
- Añadido `SaveOutput` para guardar el video concatenado
- Color matching y temporal blend re-habilitados (nodos custom SÍ existen en `comfy_node/video_concat_nodes.py`)
- Audio del primer video preservado en el resultado

## Arquitectura Final

1. **Prioridad 11:** Image To Video genera el primer video (nativo SwarmUI)
2. **Prioridad 11.5:** VideoConcat toma ese video y genera continuaciones:
   - Para cada sección adicional (prompt diferente):
     - Extrae últimos N frames del video anterior
     - Genera nuevo video con el prompt de la sección
     - Aplica color matching con la sección anterior
     - Añade al array de chunks
   - Concatena todos los chunks con `ImageBatch`
   - Aplica temporal blending final
   - Guarda el resultado con audio del primer video

## Nodos ComfyUI Incluidos

En `comfy_node/video_concat_nodes.py`:
- `VideoColorMatch` - Matching de histograma de color entre videos
- `VideoTemporalBlend` - Suavizado temporal para reducir flickering
- `VideoCrossFadeTransition` - Transiciones crossfade entre videos
- `VideoBatch` - Batching de frames
- `EmptyLatentVideo` - Latent vacío para generación

## Parámetros

| Parámetro | Tipo | Default | Descripción |
|-----------|------|---------|-------------|
| `Section Prompts` | string | - | Prompts separados por `\|\|\|` (mínimo 2) |
| `Section Durations` | string | - | Frames por sección (opcional, usa Video Frames) |
| `Transition Frames` | int | 12 | Overlap entre secciones |
| `Enable Color Matching` | bool | true | Match de color entre secciones |
| `Color Match Strength` | double | 0.5 | Intensidad del color matching (0-1) |
| `Enable Temporal Blending` | bool | true | Blending temporal |
| `Temporal Blend Strength` | double | 0.5 | Intensidad del blending (0-1) |

## TODO / Próximos Pasos

### Prioridad Alta
- [ ] **Testing real** - Probar con un modelo i2v real (Wan, LTXV, etc.)
- [ ] **Verificar nodos ComfyUI** - Asegurar que los nodos custom se cargan correctamente
- [ ] **Validar flujo de frames** - Verificar que el encadenamiento funciona como Video Extend

### Prioridad Media
- [ ] **UI mejorada** - Editor visual para secciones (actualmente es solo input de texto)
- [ ] **Vista previa** - Mostrar duración total calculada antes de generar
- [ ] **Soporte para SaveIntermediate** - Guardar secciones intermedias

### Prioridad Baja
- [ ] **Soporte para video existente como input** - Permitir cargar videos preexistentes
- [ ] **Audio handling** - Preservar/manejar audio de videos
- [ ] **Optimización de memoria** - Procesar secciones secuencialmente para reducir VRAM

## Problemas Conocidos

1. **Nodos custom no verificados en ComfyUI** - Los nodos Python están creados pero no probados en runtime
2. **Color Match y Temporal Blend aplican sobre videos ya concatenados** - Podría ser mejor aplicar por sección antes de concatenar
3. **Sin manejo de archivos de video reales** - Solo genera desde imagen inicial, no acepta videos como input

## Cómo Probar

1. Seleccionar un modelo en **Image To Video** (ej: Wan 2.1, LTXV)
2. Cargar una imagen inicial
3. Habilitar **Video Concatenation** en el grupo de parámetros
4. Configurar:
   ```
   Section Durations: 25,25,25
   Section Prompts: A cat walking|||A cat running|||A cat jumping
   Transition Frames: 12
   ```
5. Generar

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