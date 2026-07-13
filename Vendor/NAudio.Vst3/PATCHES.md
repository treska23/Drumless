# NAudio.Vst3 incorporado temporalmente

Este directorio contiene el módulo `NAudio.Vst3` de NAudio 3.0.0-preview.16,
compilado como parte de Drumless mientras la implementación pública continúa
en previsualización.

Cambio local:

- `Interop/Events/Events.cs`: la unión de `Event` ocupa 24 bytes para que la
  estructura completa mida 48 bytes en Windows x64, como exige
  `SMTG_TYPE_SIZE_CHECK(Event, 48, 40, 40, 48)` del SDK oficial VST3. La
  versión publicada reservaba 48 bytes para la unión y producía una estructura
  de 72 bytes, capaz de sobrescribir memoria cuando un VST3 leía la primera
  nota MIDI.

Origen: https://github.com/naudio/NAudio

Licencia: MIT; consulta `LICENSE` en este mismo directorio.
