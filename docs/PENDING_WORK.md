# Trabajo pendiente acordado

Estado guardado el 16 de julio de 2026 para continuar a partir del día 23.

## YouTube

### Sesión persistente

- Objetivo: iniciar sesión una vez y conservar el login entre búsquedas y ejecuciones.
- Implementado: WebView2 usa siempre `AppPaths.YouTubeWebViewData`, fuera de `bin` y `obj`;
  se verifica que no sea InPrivate y se usa prevención de seguimiento `Basic` para mejorar
  la compatibilidad de las cookies de Google/YouTube.
- Compatibilidad: se conserva deliberadamente el perfil predeterminado ya existente. No se
  debe asignar un `ProfileName`: hacerlo crea `WV2Profile_default` y abandona las cookies del
  perfil histórico `Default`.
- Pendiente: prueba manual iniciando sesión, cerrar completamente la aplicación, volver a
  abrirla y realizar dos búsquedas. Nunca copiar ni serializar cookies en el estado JSON.

### Importar una playlist completa

- Objetivo: pegar una URL con `list=...` y añadir todos sus vídeos a la playlist activa del
  programa con una sola acción.
- Implementado: navegación directa al pegar URL, botón `Importar playlist completa`, carga
  progresiva de elementos del DOM de YouTube, mantenimiento del orden, miniaturas y omisión
  de duplicados. Se guarda una sola vez al terminar.
- Validado: compilación Release y pruebas de reconocimiento de URL, orden y duplicados.
- Pendiente: terminar la prueba visual contra una playlist pública larga y ajustar selectores
  si YouTube cambia su DOM. Debe mostrar descubiertos, añadidos y duplicados.

### Reproducir desde la playlist sin abandonar la pantalla

- Fallo confirmado: `OnYouTubePlaybackRequested` llama actualmente a `OpenYouTubePage()` y
  cambia la sección visible. Además, navegar al vídeo no garantiza `video.play()` ni audio.
- Criterio de aceptación:
  1. Pulsar reproducir en una fila YouTube mantiene la pantalla actual.
  2. El WebView oculto carga/reutiliza el vídeo y comienza reproducción con sonido.
  3. Si ya está cargado, reanuda sin recargar.
  4. Pausa, parada, anterior, siguiente y final natural siguen funcionando.
  5. El encabezado refleja reproducción real, no sólo una navegación solicitada.
- Implementación prevista: eliminar `OpenYouTubePage()`, añadir estado de reproducción
  pendiente, solicitar autoplay en la URL y ejecutar `video.play()` al completar navegación;
  comprobar el mensaje `video-state` antes de anunciar que reproduce.

## Interfaz de playlists

- Fallo confirmado mediante captura: la última fila de la playlist central no siempre es
  alcanzable; aparece además una barra horizontal que resta altura útil.
- Criterio de aceptación:
  1. Todas las filas, incluida la última, son accesibles con rueda, barra y teclado.
  2. La rueda sobre la lista desplaza su scroll interno, no el scroll exterior.
  3. No aparece barra horizontal; las columnas se adaptan al ancho.
  4. Funciona con escalado de Windows y ventana pequeña.
  5. La ventana flotante conserva el mismo comportamiento.
- Implementación prevista: desactivar scroll horizontal, estirar `ListBoxItem`, fijar una
  superficie vertical estable y encaminar `PreviewMouseWheel` al `ScrollViewer` interno.

## Tempo, claqueta y búsqueda asistida

### Problema

- El analizador actual produce un BPM global y no es fiable con intros, síncopas,
  medio/doble tempo ni cambios dentro de la canción.
- Llama/Ollama no navega por Internet por sí solo. Sólo debe contrastar resultados reales
  que incluyan URL y texto de fuente; nunca inventar el BPM.

### Mapa de tempo por secciones

- Sustituir el BPM único por segmentos persistentes: tiempo exacto de inicio, BPM, compás,
  fase/primer pulso y confianza/origen.
- Permitir añadir, editar, mover y borrar cambios manualmente.
- El analizador local debe trabajar por ventanas y proponer segmentos, fusionando tramos
  estables y marcando incertidumbre; el usuario confirma antes de aplicar.
- La claqueta local y la de YouTube deben cambiar de segmento según la posición actual,
  mantener continuidad de fase al pausar/buscar y no derivar en los límites.
- Las sesiones de puntuación deben guardar la versión del mapa utilizada.

### Búsqueda online con fuentes

- Buscar por artista y título normalizados, además del nombre de archivo.
- Mostrar cada candidato con BPM, posibles cambios por sección, fuente y enlace.
- No aplicar automáticamente: el usuario selecciona y confirma.
- Conectar opcionalmente a Ollama local para agrupar/contrastar candidatos y detectar
  desacuerdos o medio/doble tempo. Si Ollama no está disponible, la búsqueda sigue funcionando.
- No utilizar una respuesta de IA sin fuente como dato de tempo.

## Validación requerida al retomar

- Compilación Debug y Release con .NET 10: cero errores y cero advertencias.
- Pruebas de persistencia, importación masiva, duplicados, scroll, reproducción oculta,
  final natural, mapas de tempo, cambio de sección y búsqueda sin Ollama.
- Prueba real de interfaz con una playlist larga local, una playlist larga de YouTube y
  reproducción de un vídeo permaneciendo en `Pistas locales`.
