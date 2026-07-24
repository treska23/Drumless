# Third-party notices

Drumless can optionally install local source-separation engines in private application folders.
They are not bundled in the repository or executable and are downloaded only after user confirmation.

## Audio Separator

- Project: `nomadkaraoke/python-audio-separator`
- Version selected by Drumless: `0.44.5`
- License: MIT
- Purpose: local inference with compatible UVR source-separation models and ensemble presets.

## Ultimate Vocal Remover models

The advanced lead/backing-vocal workflow uses the `karaoke` ensemble exposed by Audio Separator.
Credit belongs to the Ultimate Vocal Remover project, its core developers and the individual model authors.
Model files are downloaded on first use and are not redistributed by Drumless.

## librosa / SciPy / SoundFile

These libraries are installed inside the optional advanced runtime for local audio analysis,
melody masking and WAV input/output. Their respective upstream licenses apply.

## Demucs

Drumless uses Demucs 4.0.1 as the first-stage six-stem separator. Its existing license and notices apply.
