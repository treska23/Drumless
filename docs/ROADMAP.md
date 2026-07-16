# Hoja de ruta

## Implementado en la rama actual

- Playlists mixtas con pistas locales y vídeos de YouTube.
- Playlist central, reordenación por arrastre y ventana flotante.
- Mezclas libres de seis stems de Demucs: batería, bajo, voz, guitarra, piano/teclados y otros.
- Monitorización simultánea de todas las entradas ASIO con ganancia independiente.
- Grabación WAV de la mezcla local, incluidas entradas monitorizadas y batería interna/VST.
- Tempo y primer pulso bajo demanda, claqueta local y claqueta manual sincronizada con
  el reloj del vídeo de YouTube.
- Evaluación de golpes MIDI con compensación de latencia e historial persistente por pista.
- Base de datos JSON versionada para análisis y sesiones; una misma clave de vídeo comparte
  datos entre playlists.

## Implementado y verificado

### Claqueta sincronizada por pista

- El usuario decide qué pistas analizar; no se analiza toda la biblioteca automáticamente.
- Detección de BPM y posición del primer pulso, con edición manual de ambos valores.
- Claqueta ligada a la posición del transporte para conservar la sincronía al pausar,
  continuar o buscar dentro de la pista.
- En vídeos de YouTube se ofrecen BPM y primer pulso manuales. El análisis automático
  del audio del vídeo no se considerará fiable mientras el navegador sea quien lo reproduce.

### Evaluación de precisión de batería

- Solo para interpretación de batería.
- Fuente preferente: golpes MIDI de una batería electrónica. Como alternativa, detección
  de transitorios en una entrada de audio dedicada y calibrada.
- Comparación contra la rejilla rítmica de la pista analizada, compensando la latencia
  completa de entrada y salida.
- Resultado al terminar: porcentaje de precisión, golpes adelantados y atrasados y error
  temporal medio. Cada sesión se guarda con fecha, latencia usada y final natural/manual.
- Modos posteriores: práctica libre, reto/juego, historial de mejora y asistente tipo profesor.
- La puntuación actual requiere BPM, primer pulso y compensación de latencia configurados.
  Es una medida de colocación sobre la rejilla, no una transcripción completa de la parte.

## Ampliaciones posteriores

- Mapas de tempo para canciones con acelerando, ritardando o cambios de BPM.
- Transcripción de una batería de referencia para distinguir golpes omitidos de silencios
  intencionados. La versión actual puntúa la colocación temporal de los golpes ejecutados.
- Captura de audio de YouTube: WebView2 reproduce fuera del mezclador local/ASIO, por lo
  que la grabación directa se limita deliberadamente a pistas locales para no generar una
  toma silenciosa o incompleta.
