# Hoja de ruta

## Implementado en la rama actual

- Playlists mixtas con pistas locales y vídeos de YouTube.
- Playlist central, reordenación por arrastre y ventana flotante.
- Mezclas libres de seis stems de Demucs: batería, bajo, voz, guitarra, piano/teclados y otros.
- Monitorización simultánea de todas las entradas ASIO con ganancia independiente.
- Perfiles independientes Limpio, Voz, Guitarra limpia, Guitarra con distorsión, Bajo y
  Batería, con cadenas incorporadas de hasta cuatro procesos.
- Grabación WAV de la mezcla local o YouTube, incluidas entradas monitorizadas y
  batería interna/VST.
- Sesión persistente de YouTube, importación de playlists completas, reproducción oculta
  desde cualquier pantalla y audio dirigido al mezclador/salida elegidos.
- Tempo y primer pulso bajo demanda, claqueta local y claqueta manual sincronizada con
  el reloj del vídeo de YouTube.
- Evaluación de golpes MIDI con compensación de latencia e historial persistente por pista.
- Base de datos JSON versionada para análisis y sesiones; una misma clave de vídeo comparte
  datos entre playlists.
- Cadenas editables de cuatro slots por entrada y por buses de pista, YouTube y maestro,
  con bypass A/B, reordenación, parámetros y presets importables/exportables.
- Efectos VST3 externos aislados; un fallo deja el slot en bypass sin detener el audio.
- Mapas de tempo persistentes por tramos, editor manual, propuesta automática por ventanas
  y claqueta/puntuación con cambio de tempo y fase continua.
- Búsqueda de tempo con fuentes enlazadas y contraste opcional mediante Ollama local.
- Referencia de batería analizada para contar golpes omitidos y adicionales.

## Implementado y verificado

### Claqueta sincronizada por pista

- El usuario decide qué pistas analizar; no se analiza toda la biblioteca automáticamente.
- Detección de BPM y posición del primer pulso, con edición manual de ambos valores.
- Claqueta ligada a la posición del transporte para conservar la sincronía al pausar,
  continuar o buscar dentro de la pista.
- En vídeos de YouTube se pueden aplicar mapas manuales o candidatos de fuentes web; el
  reloj del vídeo alimenta la claqueta y la evaluación sin cambiar de pantalla.

### Evaluación de precisión de batería

- Solo para interpretación de batería.
- Fuente preferente: golpes MIDI de una batería electrónica. Como alternativa, detección
  de transitorios en una entrada de audio dedicada y calibrada.
- Comparación contra la rejilla rítmica o una pista de batería de referencia, compensando
  la latencia de entrada, salida y efectos aislados.
- Resultado al terminar: porcentaje de precisión, golpes adelantados y atrasados y error
  temporal medio, omitidos y adicionales. Cada sesión se guarda con fecha, latencia,
  versión de referencia y final natural/manual.
- La puntuación actual requiere BPM, primer pulso y compensación de latencia configurados.
  Sin pista de referencia es una medida de colocación sobre la rejilla; con referencia
  también detecta golpes omitidos y extra.

## Ampliaciones posteriores

- Modos de reto/juego, tendencias de mejora y asistente tipo profesor sobre el historial.
- Editor gráfico avanzado de automatización de parámetros de efectos.
- Análisis musical especializado para compases irregulares y tempo libre.
