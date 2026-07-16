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
- Separación local opcional con Demucs y mezcla libre de batería, bajo, voz y otros;
  nunca modifica ni mueve el archivo original.
- Monitorización simultánea de todas las entradas de una interfaz ASIO, cada una con
  activación y ganancia independientes.
- Grabación WAV de la mezcla final de pistas locales: pista, batería interna/VST y
  todas las entradas ASIO monitorizadas; cada toma se registra en la biblioteca.
- Análisis de tempo bajo demanda, BPM y primer pulso editables, claqueta ligada a la
  posición exacta del transporte y evaluación temporal de golpes de batería MIDI.
- Búsqueda y reproducción de YouTube dentro de la aplicación mediante el sitio oficial
  integrado con WebView2, sin claves de API guardadas.

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

La batería cubre persistencia, escaneo y desaparecidos, playlists y reordenación,
reproducción secuencial/aleatoria y las garantías de concurrencia del transporte.

## Límites actuales

La primera separación solicita instalar Demucs, Python y PyTorch en el directorio
privado de datos de la aplicación. Los kits de
demostración usan sonidos sintetizados al iniciar y el usuario puede sustituirlos por
sus propios WAV. El alojamiento VST3 de NAudio 3 todavía es una función preview; un
plugin defectuoso puede cerrar el proceso porque los instrumentos se cargan dentro de
la propia aplicación. Groove Agent SE puede estar limitado a anfitriones Steinberg;
Groove Agent completo y Addictive Drums 2 son los objetivos principales.
