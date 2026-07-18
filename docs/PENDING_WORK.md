# Trabajo pendiente acordado

## Fiabilidad de la interfaz de audio

- Implementado el 18 de julio de 2026:
  - escucha de paradas inesperadas de WASAPI y ASIO, incluidas solicitudes de reinicio ASIO;
  - aviso persistente dentro de la aplicación con backend, tipo y código del error;
  - pausa segura de la pista y cierre de una grabación activa antes de recuperar;
  - tres intentos escalonados de reconexión a la misma salida, sin reanudar la música por sorpresa;
  - registro diagnóstico en `Logs/audio.log` y posibilidad de reintentar o elegir otra salida.
- Pendiente: prueba física prolongada desconectando o reiniciando la interfaz real y comprobando
  distintos fallos del driver. No se debe cambiar silenciosamente a otra tarjeta.

## Central de audio y efectos por entrada

- Implementado el 18 de julio de 2026:
  - perfil independiente y persistente para cada entrada ASIO;
  - perfiles Limpio, Voz, Guitarra limpia, Guitarra con distorsión, Bajo y Batería;
  - cadenas incorporadas de cuatro procesos como máximo, ejecutadas antes de mezclar la entrada;
  - cambio de perfil en tiempo real y descripción visible de la cadena aplicada.
- Pendiente:
  - controles avanzados para editar, reordenar, omitir y guardar variantes de los cuatro slots;
  - cadenas separadas para pista local, YouTube y bus maestro;
  - carga de efectos VST3 externos, aislados para que un plugin defectuoso no detenga el motor;
  - compensación de latencia, presets personalizados y comparación con y sin efectos;
  - prueba física de todos los perfiles con voz, guitarra, bajo y módulo de batería.

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

### Enrutar el audio de YouTube por la salida elegida en la aplicación

- Causa confirmada por el usuario: WebView2 envía actualmente el audio al dispositivo de
  salida predeterminado de Windows, no a `SelectedAudioOutputDevice` ni al motor de Drum
  Practice Studio. Por eso el vídeo puede estar reproduciéndose correctamente y no oírse en
  la interfaz ASIO/WASAPI seleccionada dentro del programa.
- No basta con cambiar el volumen o con mantener oculto el WebView. El audio se genera en un
  proceso de WebView2 separado y debe capturarse/enrutarse hacia el mezclador de la aplicación.
- Criterio de aceptación:
  1. YouTube suena por la misma salida WASAPI o ASIO seleccionada en Drum Practice Studio.
  2. Cambiar la salida dentro de la aplicación redirige también YouTube sin cambiar el
     dispositivo predeterminado de Windows.
  3. Pista local, YouTube, VST/batería y entradas monitorizadas conservan niveles separados.
  4. Pausa, parada, anterior, siguiente y cambio rápido de vídeo no dejan audio huérfano ni
     dos vídeos sonando a la vez.
  5. El estado visible sólo indica `Reproduciendo` cuando el audio está realmente activo.
  6. Si YouTube entra en el mezclador local, la grabación de la salida debe poder incluirlo
     de forma explícita y sin crear una toma silenciosa.
  7. Se debe medir y compensar la latencia adicional para no desincronizar claqueta, mapa de
     tempo ni puntuación de batería.
- Implementación a investigar al retomar:
  - Preferencia: captura de audio del proceso/árbol de procesos WebView2 mediante loopback por
    proceso, conversión al formato flotante del motor e inyección en `AudioOutputSession`.
  - Para salida ASIO, el flujo capturado debe entrar en el mezclador ASIO de la aplicación;
    no debe reproducirse simultáneamente por Windows para evitar eco o duplicado.
  - Alternativa sólo si la captura por proceso no es viable: política de enrutamiento por
    sesión de audio de Windows, verificando que funcione con los procesos secundarios de
    WebView2 y sobreviva a sus reinicios.
  - No descargar, extraer ni volver a servir el contenido de YouTube como solución implícita.

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
  enrutamiento WASAPI/ASIO, ausencia de audio duplicado, final natural, mapas de tempo,
  cambio de sección y búsqueda sin Ollama.
- Prueba real de interfaz con una playlist larga local, una playlist larga de YouTube y
  reproducción de un vídeo permaneciendo en `Pistas locales`.
