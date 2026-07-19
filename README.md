# Drum Practice Studio

Prototipo Windows de una aplicación de práctica para batería electrónica y teclado MIDI.

## Funciones

- Librerías y kits independientes del perfil MIDI.
- Dos kits de demostración generados localmente, sin samples de terceros.
- Pads tocables con ratón o mediante un dispositivo MIDI.
- Capas de velocidad, round-robin y grupos de choke preparados en el modelo.
- Importación rápida de un sample WAV a un pad.
- Importación de kits desde carpetas o ZIP con capas de velocidad y round-robin.
- Alojamiento de instrumentos VST3 de 64 bits, con búsqueda de Addictive Drums 2 y
  Groove Agent, MIDI dinámico y apertura de la interfaz nativa del instrumento.
- Biblioteca persistente de pistas locales, con detección de archivos desaparecidos.
- Carpeta configurable para las pistas generadas y escaneo automático al iniciar.
- Varias playlists persistentes con audio local y vídeos de YouTube en la misma cola,
  edición central, arrastre, ventana flotante y modos individual, secuencial y aleatorio
  sin repeticiones.
- Transporte con anterior, siguiente y avance automático al final natural de una pista.
- Separación local opcional con Demucs de seis stems y mezcla libre de batería, bajo,
  voz, guitarra, piano/teclados y otros;
  nunca modifica ni mueve el archivo original.
- Monitorización simultánea de todas las entradas de una interfaz ASIO, cada una con
  activación, ganancia y etiqueta de instrumento independiente. Las cadenas admiten hasta
  cuatro plugins VST3 instalados por el usuario, reordenación por arrastre,
  envío directo al principio/final, bypass A/B y presets
  importables/exportables; no se aplican efectos internos predeterminados. Las ganancias
  usan faders verticales de estilo mesa de mezclas con clic preciso y arrastre; la claqueta
  usa un knob circular.
- Plugins VST3 externos en cada entrada y en los buses de pista local, YouTube y maestro,
  localizados en las carpetas estándar o en carpetas añadidas por el usuario. Se ejecutan
  en un proceso aislado: si fallan,
  la señal continúa en bypass y la aplicación muestra el diagnóstico.
- Grabación WAV de la mezcla final de pistas locales o YouTube: pista/vídeo,
  batería interna/VST y todas las entradas ASIO monitorizadas; cada toma se registra
  inmediatamente en la biblioteca.
- Análisis de tempo bajo demanda con mapa editable por tramos, BPM, compás, primer pulso,
  confianza y fuente. La claqueta mantiene la fase al cambiar de tramo, pausar o buscar.
- Búsqueda web de BPM con enlace y evidencia verificables; Ollama local puede ordenar y
  comentar los candidatos, pero no inventar un tempo ni sustituir la fuente.
- Evaluación temporal de golpes de batería MIDI contra la rejilla o contra una pista de
  batería de referencia analizada, con omitidos, golpes extra, latencia e historial.
- Base de datos JSON local y versionada para conservar el tempo, origen y confianza del
  análisis, ajustes de claqueta e historial fechado de puntuaciones por pista o vídeo.
  Quitar un elemento de una playlist conserva esos datos; quitar una pista de la biblioteca
  los borra sin eliminar el archivo de audio.
- Búsqueda y reproducción de YouTube dentro de la aplicación mediante el sitio oficial
  integrado con WebView2, sesión persistente e importación de playlists completas.
  La reproducción iniciada desde una playlist no cambia de pantalla y su audio se captura
  de forma aislada, entra en el mezclador y usa la salida WASAPI/ASIO elegida.

## Ejecutar

```powershell
dotnet run
```

Requiere Windows de 64 bits y el SDK de .NET 10. El proyecto usa `net10.0-windows`
y NAudio 3 preview para alojar VST3 dentro del mismo mezclador WASAPI. Los instrumentos
se buscan en las ubicaciones VST3 estándar de Windows, principalmente
`C:\Program Files\Common Files\VST3`.

## Pruebas

```powershell
dotnet test DrumPracticeStudio.sln
```

La batería cubre persistencia y migración de análisis, mapas de tempo, fuentes web,
escaneo y desaparecidos, playlists y reordenación, reproducción secuencial/aleatoria,
transporte, cadenas de entrada y buses, puntuación con referencia y los procesos aislados
de YouTube y VST3.

## Límites actuales

La primera separación solicita instalar Demucs, Python y PyTorch en el directorio
privado de datos de la aplicación. Los kits de
demostración usan sonidos sintetizados al iniciar y el usuario puede sustituirlos por
sus propios WAV. El alojamiento VST3 de NAudio 3 todavía es una función preview; un
plugin defectuoso puede cerrar el proceso porque los instrumentos se cargan dentro de
la propia aplicación. Groove Agent SE puede estar limitado a anfitriones Steinberg;
Groove Agent completo y Addictive Drums 2 son los objetivos principales. Esta limitación
afecta a los instrumentos VST3; los efectos VST3 se alojan en procesos independientes.
La detección automática de tempo es una propuesta que el usuario debe revisar antes de
aplicar, especialmente en grabaciones con rubato o cambios poco marcados.
