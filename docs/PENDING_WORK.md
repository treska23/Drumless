# Trabajo pendiente acordado

Estado revisado el 18 de julio de 2026.

## Cerrado y verificado

- Solución .NET 10 copiada y mantenida únicamente en `D:\OneDrive\Documentos\Drumless`.
- Biblioteca persistente, carpeta configurable, escaneo inicial y aviso de desaparecidos.
- Playlists mixtas locales/YouTube, creación, renombrado, borrado sin eliminar audio,
  altas/bajas, orden por botones y arrastre, ventana flotante y scroll completo.
- Modos individual, secuencial y aleatorio sin repeticiones; anterior, siguiente y avance
  por final natural con protección ante cargas rápidas.
- Demucs de seis stems con selección libre de qué instrumentos conservar.
- Todas las entradas ASIO utilizables simultáneamente, con ganancia y perfil persistentes.
- Presets de entrada Limpio, Voz, Guitarra limpia, Guitarra con distorsión, Bajo y Batería;
  sus cadenas incorporadas usan hasta cuatro procesos antes de mezclar.
- Grabación de la mezcla final con pista local o YouTube, batería/VST e inputs monitorizados.
- WebView2 con sesión persistente, búsqueda, importación de playlist completa y reproducción
  desde la playlist sin abandonar la pantalla actual.
- Audio de YouTube capturado en un proceso auxiliar aislado, inyectado en el mezclador
  WASAPI/ASIO elegido y validado con una toma WAV no silenciosa a 48 kHz estéreo.
- Aviso y recuperación ante parada inesperada de WASAPI/ASIO, con diagnóstico persistente.
- BPM y primer pulso bajo demanda, edición manual, claqueta sincronizada y puntuación MIDI
  persistente por pista o vídeo.
- Editor de cuatro slots por entrada y buses de pista, YouTube y maestro: orden, bypass,
  parámetros, A/B y presets importables/exportables.
- Efectos VST3 externos aislados, compensación de latencia y bypass automático ante fallo.
- Mapa persistente de segmentos con inicio, BPM, compás, fase, confianza y fuente; editor,
  propuesta automática por ventanas y sincronización al pausar, buscar o cambiar de tramo.
- Búsqueda online con URL y evidencia; Ollama local opcional para contrastar candidatos.
- Análisis de una pista de batería de referencia y puntuación de golpes correctos,
  adelantados, atrasados, omitidos y adicionales.

## Validación física pendiente

- Sesión prolongada reiniciando y desconectando la interfaz real.
- Prueba auditiva de cada preset con voz, guitarra, bajo y módulo de batería.
- Playlist pública de YouTube muy larga para vigilar cambios futuros del DOM.
