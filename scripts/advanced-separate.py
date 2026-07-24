from __future__ import annotations

import argparse
import json
import math
import shutil
import sys
import tempfile
from pathlib import Path

import librosa
import numpy as np
import soundfile as sf
from scipy.ndimage import gaussian_filter, median_filter


def report(message: str, percent: float | None = None) -> None:
    payload = {"message": message}
    if percent is not None:
        payload["percent"] = max(0.0, min(1.0, float(percent)))
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def ensure_stereo(audio: np.ndarray) -> np.ndarray:
    if audio.ndim == 1:
        return np.vstack([audio, audio])
    if audio.shape[0] == 1:
        return np.vstack([audio[0], audio[0]])
    return audio[:2]


def peak_normalize(audio: np.ndarray, ceiling: float = 0.98) -> np.ndarray:
    peak = float(np.max(np.abs(audio))) if audio.size else 0.0
    if peak <= ceiling or peak <= 1e-9:
        return audio
    return audio * (ceiling / peak)


def split_vocals(vocals_path: Path, output_dir: Path, model_dir: Path) -> tuple[Path, Path]:
    from audio_separator.separator import Separator

    report("Voz avanzada · preparando modelos UVR karaoke", 0.08)
    temp_dir = Path(tempfile.mkdtemp(prefix="drumless-vocals-", dir=output_dir))
    try:
        separator = Separator(
            output_dir=str(temp_dir),
            model_file_dir=str(model_dir),
            output_format="WAV",
            sample_rate=44_100,
            use_soundfile=True,
            ensemble_preset="karaoke",
        )
        separator.load_model()
        report("Voz avanzada · separando voz principal y coros", 0.18)
        output_names = {
            "Vocals": "lead-vocal",
            "Instrumental": "back-vocal",
        }
        generated = [Path(path) for path in separator.separate(str(vocals_path), output_names)]

        lead_source = next((path for path in generated if path.stem.lower() == "lead-vocal"), None)
        back_source = next((path for path in generated if path.stem.lower() == "back-vocal"), None)
        if lead_source is None:
            lead_source = next((path for path in generated if "(vocals)" in path.name.lower()), None)
        if back_source is None:
            back_source = next((path for path in generated if "(instrumental)" in path.name.lower()), None)
        if lead_source is None or back_source is None:
            names = ", ".join(path.name for path in generated)
            raise RuntimeError(f"Audio Separator no devolvió los stems vocales esperados: {names}")

        lead_target = output_dir / "lead-vocal.wav"
        back_target = output_dir / "back-vocal.wav"
        shutil.copy2(lead_source, lead_target)
        shutil.copy2(back_source, back_target)
        return lead_target, back_target
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def build_melody_mask(mono: np.ndarray, sample_rate: int, n_fft: int, hop_length: int) -> np.ndarray:
    harmonic = librosa.effects.harmonic(mono, margin=2.0)
    spectrum = librosa.stft(harmonic, n_fft=n_fft, hop_length=hop_length, window="hann")
    magnitude = np.abs(spectrum)
    frequencies = librosa.fft_frequencies(sr=sample_rate, n_fft=n_fft)

    pitches, magnitudes = librosa.piptrack(
        S=magnitude,
        sr=sample_rate,
        n_fft=n_fft,
        hop_length=hop_length,
        fmin=70.0,
        fmax=min(2400.0, sample_rate / 2.2),
        threshold=0.08,
    )

    frame_count = magnitude.shape[1]
    f0 = np.zeros(frame_count, dtype=np.float32)
    confidence = np.zeros(frame_count, dtype=np.float32)
    spectral_floor = np.percentile(magnitude, 75, axis=0) + 1e-8

    previous = 0.0
    for frame in range(frame_count):
        candidates = np.flatnonzero(pitches[:, frame] > 0)
        if candidates.size == 0:
            continue
        candidate_pitches = pitches[candidates, frame]
        candidate_magnitudes = magnitudes[candidates, frame]
        score = candidate_magnitudes.copy()
        score *= np.clip(candidate_pitches / 220.0, 0.65, 2.4)
        if previous > 0:
            distance = np.abs(12.0 * np.log2(np.maximum(candidate_pitches, 1.0) / previous))
            score *= np.exp(-0.055 * np.minimum(distance, 24.0))
        winner = int(np.argmax(score))
        selected_index = candidates[winner]
        f0[frame] = float(pitches[selected_index, frame])
        confidence[frame] = float(
            np.clip(magnitudes[selected_index, frame] / spectral_floor[frame], 0.0, 1.0)
        )
        previous = float(f0[frame])

    voiced = f0 > 0
    if np.any(voiced):
        filled = f0.copy()
        indices = np.arange(frame_count)
        filled[~voiced] = np.interp(indices[~voiced], indices[voiced], f0[voiced])
        filled = median_filter(filled, size=7, mode="nearest")
        short_gap = gaussian_filter(voiced.astype(np.float32), sigma=1.4) > 0.28
        f0 = np.where(short_gap, filled, 0.0)
        confidence = gaussian_filter(confidence, sigma=1.2)

    mask = np.zeros_like(magnitude, dtype=np.float32)
    bin_width = sample_rate / n_fft
    for frame, fundamental in enumerate(f0):
        if fundamental <= 0:
            continue
        frame_confidence = max(0.18, float(confidence[frame]))
        harmonic_number = 1
        while harmonic_number <= 16:
            harmonic_frequency = fundamental * harmonic_number
            if harmonic_frequency >= frequencies[-1]:
                break
            centre = harmonic_frequency / bin_width
            semitone_width = 0.58 + min(1.25, harmonic_number * 0.055)
            frequency_ratio = math.pow(2.0, semitone_width / 12.0) - 1.0
            radius = max(2.0, centre * frequency_ratio)
            low = max(0, int(math.floor(centre - radius * 2.3)))
            high = min(mask.shape[0], int(math.ceil(centre + radius * 2.3)) + 1)
            if high > low:
                bins = np.arange(low, high, dtype=np.float32)
                gaussian = np.exp(-0.5 * np.square((bins - centre) / max(radius, 1.0)))
                harmonic_weight = frame_confidence / math.sqrt(harmonic_number)
                mask[low:high, frame] = np.maximum(
                    mask[low:high, frame],
                    gaussian * harmonic_weight,
                )
            harmonic_number += 1

    local_peak = magnitude / (gaussian_filter(magnitude, sigma=(7.0, 1.3)) + 1e-8)
    prominence = np.clip((local_peak - 0.9) / 2.4, 0.0, 1.0)
    mask *= 0.48 + (0.52 * prominence)
    mask = gaussian_filter(mask, sigma=(1.15, 1.1))
    return np.clip(mask, 0.0, 0.94)


def split_guitar_chunk(chunk: np.ndarray, sample_rate: int) -> tuple[np.ndarray, np.ndarray]:
    chunk = ensure_stereo(chunk)
    n_fft = 4096 if sample_rate >= 32_000 else 2048
    hop_length = n_fft // 8
    mono = np.mean(chunk, axis=0)

    base_mask = build_melody_mask(mono, sample_rate, n_fft, hop_length)
    left_spectrum = librosa.stft(chunk[0], n_fft=n_fft, hop_length=hop_length, window="hann")
    right_spectrum = librosa.stft(chunk[1], n_fft=n_fft, hop_length=hop_length, window="hann")
    mid_spectrum = (left_spectrum + right_spectrum) * 0.5
    side_spectrum = (left_spectrum - right_spectrum) * 0.5
    centre_ratio = np.abs(mid_spectrum) / (np.abs(mid_spectrum) + np.abs(side_spectrum) + 1e-8)

    lead_mask = np.clip(base_mask * (0.58 + 0.42 * centre_ratio), 0.0, 0.92)
    lead_mask = gaussian_filter(lead_mask, sigma=(0.7, 0.8))
    rhythm_mask = 1.0 - lead_mask

    lead = np.vstack(
        [
            librosa.istft(left_spectrum * lead_mask, hop_length=hop_length, length=chunk.shape[1]),
            librosa.istft(right_spectrum * lead_mask, hop_length=hop_length, length=chunk.shape[1]),
        ]
    )
    rhythm = np.vstack(
        [
            librosa.istft(left_spectrum * rhythm_mask, hop_length=hop_length, length=chunk.shape[1]),
            librosa.istft(right_spectrum * rhythm_mask, hop_length=hop_length, length=chunk.shape[1]),
        ]
    )
    return lead.astype(np.float32), rhythm.astype(np.float32)


def split_guitar(guitar_path: Path, output_dir: Path) -> tuple[Path, Path]:
    report("Guitarra avanzada · analizando melodía y acompañamiento", 0.58)
    audio, sample_rate = sf.read(guitar_path, dtype="float32", always_2d=True)
    stereo = ensure_stereo(audio.T)
    sample_count = stereo.shape[1]

    chunk_samples = int(sample_rate * 28.0)
    overlap_samples = int(sample_rate * 1.5)
    lead_accumulator = np.zeros_like(stereo, dtype=np.float32)
    rhythm_accumulator = np.zeros_like(stereo, dtype=np.float32)
    weights = np.zeros(sample_count, dtype=np.float32)

    starts = list(range(0, sample_count, max(1, chunk_samples - overlap_samples)))
    for index, start in enumerate(starts):
        end = min(sample_count, start + chunk_samples)
        chunk = stereo[:, start:end]
        lead_chunk, rhythm_chunk = split_guitar_chunk(chunk, sample_rate)

        window = np.ones(end - start, dtype=np.float32)
        fade = min(overlap_samples, (end - start) // 3)
        if fade > 1 and start > 0:
            window[:fade] = np.linspace(0.0, 1.0, fade, dtype=np.float32)
        if fade > 1 and end < sample_count:
            window[-fade:] = np.linspace(1.0, 0.0, fade, dtype=np.float32)

        lead_accumulator[:, start:end] += lead_chunk * window
        rhythm_accumulator[:, start:end] += rhythm_chunk * window
        weights[start:end] += window
        report(
            f"Guitarra avanzada · tramo {index + 1} de {len(starts)}",
            0.58 + (0.34 * ((index + 1) / max(1, len(starts)))),
        )

    weights = np.maximum(weights, 1e-7)
    lead = lead_accumulator / weights
    rhythm = rhythm_accumulator / weights

    # Preserve complementarity: any numerical reconstruction residue stays in rhythm guitar.
    residue = stereo - (lead + rhythm)
    rhythm += residue
    lead = peak_normalize(lead)
    rhythm = peak_normalize(rhythm)

    lead_target = output_dir / "lead-guitar.wav"
    rhythm_target = output_dir / "rhythm-guitar.wav"
    sf.write(lead_target, lead.T, sample_rate, subtype="PCM_16")
    sf.write(rhythm_target, rhythm.T, sample_rate, subtype="PCM_16")
    return lead_target, rhythm_target


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Separación avanzada de stems para Drumless")
    parser.add_argument("--vocals", required=True, type=Path)
    parser.add_argument("--guitar", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--models", required=True, type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    args.models.mkdir(parents=True, exist_ok=True)
    if not args.vocals.is_file():
        raise FileNotFoundError(args.vocals)
    if not args.guitar.is_file():
        raise FileNotFoundError(args.guitar)

    lead_vocal, back_vocal = split_vocals(args.vocals, args.output, args.models)
    report("Voz principal y coros preparados", 0.50)
    lead_guitar, rhythm_guitar = split_guitar(args.guitar, args.output)
    report("Separación avanzada terminada", 1.0)
    print(
        json.dumps(
            {
                "result": {
                    "leadVocal": str(lead_vocal),
                    "backVocal": str(back_vocal),
                    "leadGuitar": str(lead_guitar),
                    "rhythmGuitar": str(rhythm_guitar),
                }
            },
            ensure_ascii=False,
        ),
        flush=True,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
    except Exception as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False), file=sys.stderr, flush=True)
        raise
