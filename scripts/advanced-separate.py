from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import unquote, urlparse

import librosa
import numpy as np
import soundfile as sf
from scipy.ndimage import gaussian_filter, median_filter


KARAOKE_PRESET_RETRIES = 2
KARAOKE_TWO_MODEL_ENSEMBLE = [
    "mel_band_roformer_karaoke_aufr33_viperx_sdr_10.1956.ckpt",
    "mel_band_roformer_karaoke_gabox_v2.ckpt",
]
KARAOKE_FALLBACK_MODEL = "UVR_MDXNET_KARA_2.onnx"
KARAOKE_FALLBACK_SHA256 = "bf32e15105a09c0f7dddd2b67346146334d6f3ecb399ed7638eba2ab07cbf5f4"
KARAOKE_FALLBACK_MIRRORS = [
    "https://huggingface.co/seanghay/uvr_models/resolve/main/UVR_MDXNET_KARA_2.onnx?download=true",
    "https://huggingface.co/lissette/uvr/resolve/main/MDX-Net/UVR_MDXNET_KARA_2.onnx?download=true",
]


def report(message: str, percent: float | None = None) -> None:
    payload = {"message": message}
    if percent is not None:
        payload["percent"] = max(0.0, min(1.0, float(percent)))
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def ensure_private_ffmpeg() -> Path:
    """Makes a private ffmpeg.exe available without changing the user's Windows PATH."""
    install_root = Path(sys.executable).resolve().parents[2]
    uv_exe = install_root / "uv" / "uv.exe"
    ffmpeg_dir = install_root / "ffmpeg"
    ffmpeg_target = ffmpeg_dir / "ffmpeg.exe"

    try:
        import imageio_ffmpeg
    except ModuleNotFoundError:
        report("Preparando FFmpeg local para el motor avanzado", 0.01)
        if not uv_exe.is_file():
            raise FileNotFoundError(
                f"No se encontró uv.exe para instalar FFmpeg en {uv_exe}"
            )
        result = subprocess.run(
            [
                str(uv_exe),
                "pip",
                "install",
                "--python",
                sys.executable,
                "imageio-ffmpeg==0.6.0",
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "error desconocido").strip()
            raise RuntimeError(f"No se pudo instalar FFmpeg local: {detail}")
        importlib.invalidate_caches()
        import imageio_ffmpeg

    ffmpeg_source = Path(imageio_ffmpeg.get_ffmpeg_exe()).resolve()
    if not ffmpeg_source.is_file():
        raise FileNotFoundError(
            f"imageio-ffmpeg no encontró su ejecutable: {ffmpeg_source}"
        )

    ffmpeg_dir.mkdir(parents=True, exist_ok=True)
    if (
        not ffmpeg_target.is_file()
        or ffmpeg_target.stat().st_size != ffmpeg_source.stat().st_size
    ):
        shutil.copy2(ffmpeg_source, ffmpeg_target)

    current_path = os.environ.get("PATH", "")
    ffmpeg_dir_text = str(ffmpeg_dir)
    if ffmpeg_dir_text.lower() not in current_path.lower().split(os.pathsep):
        os.environ["PATH"] = ffmpeg_dir_text + os.pathsep + current_path
    os.environ["IMAGEIO_FFMPEG_EXE"] = str(ffmpeg_target)
    os.environ["FFMPEG_BINARY"] = str(ffmpeg_target)

    check = subprocess.run(
        [str(ffmpeg_target), "-version"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if check.returncode != 0:
        detail = (check.stderr or check.stdout or "error desconocido").strip()
        raise RuntimeError(f"El FFmpeg privado no puede ejecutarse: {detail}")

    report("FFmpeg local preparado", 0.03)
    return ffmpeg_target


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


def clean_failed_model_download(model_dir: Path, error: Exception) -> None:
    """Removes only incomplete files mentioned by a failed model download."""
    model_dir.mkdir(parents=True, exist_ok=True)
    error_text = str(error)
    for raw_url in re.findall(r"https?://[^\s]+", error_text):
        raw_url = raw_url.rstrip(".,);]}")
        file_name = unquote(Path(urlparse(raw_url).path).name)
        if not file_name:
            continue
        candidate = model_dir / file_name
        try:
            if candidate.is_file() and candidate.stat().st_size < 8 * 1024 * 1024:
                candidate.unlink()
        except OSError:
            pass

    for pattern in ("*.part", "*.tmp", "*.download"):
        for candidate in model_dir.glob(pattern):
            try:
                candidate.unlink()
            except OSError:
                pass


def resolve_generated_path(path: str | Path, output_dir: Path) -> Path:
    resolved = Path(path)
    if not resolved.is_absolute():
        resolved = output_dir / resolved
    return resolved


def resolve_vocal_outputs(generated: list[Path]) -> tuple[Path, Path]:
    lead_source = next(
        (path for path in generated if path.stem.lower() == "lead-vocal"),
        None,
    )
    back_source = next(
        (path for path in generated if path.stem.lower() == "back-vocal"),
        None,
    )
    if lead_source is None:
        lead_source = next(
            (path for path in generated if "(vocals)" in path.name.lower()),
            None,
        )
    if back_source is None:
        back_source = next(
            (path for path in generated if "(instrumental)" in path.name.lower()),
            None,
        )
    if lead_source is None or back_source is None:
        names = ", ".join(path.name for path in generated)
        raise RuntimeError(
            f"Audio Separator no devolvió los stems vocales esperados: {names}"
        )
    if not lead_source.is_file() or not back_source.is_file():
        raise FileNotFoundError(
            f"Audio Separator anunció archivos que no existen: {lead_source}, {back_source}"
        )
    return lead_source, back_source


def run_vocal_separator(
    vocals_path: Path,
    attempt_dir: Path,
    model_dir: Path,
    *,
    ensemble_preset: str | None = None,
    model_filenames: str | list[str] | None = None,
) -> tuple[Path, Path]:
    from audio_separator.separator import Separator

    attempt_dir.mkdir(parents=True, exist_ok=True)
    separator_options: dict[str, object] = {
        "output_dir": str(attempt_dir),
        "model_file_dir": str(model_dir),
        "output_format": "WAV",
        "sample_rate": 44_100,
        "use_soundfile": True,
    }
    if ensemble_preset is not None:
        separator_options["ensemble_preset"] = ensemble_preset
    elif isinstance(model_filenames, list):
        separator_options["ensemble_algorithm"] = "avg_wave"

    separator = Separator(**separator_options)
    if model_filenames is None:
        separator.load_model()
    else:
        separator.load_model(model_filename=model_filenames)

    output_names = {
        "Vocals": "lead-vocal",
        "Instrumental": "back-vocal",
    }
    generated = [
        resolve_generated_path(path, attempt_dir)
        for path in separator.separate(str(vocals_path), output_names)
    ]
    return resolve_vocal_outputs(generated)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def ensure_fallback_karaoke_model(model_dir: Path) -> Path:
    model_dir.mkdir(parents=True, exist_ok=True)
    target = model_dir / KARAOKE_FALLBACK_MODEL
    if target.is_file():
        try:
            if file_sha256(target) == KARAOKE_FALLBACK_SHA256:
                return target
        except OSError:
            pass
        try:
            target.unlink()
        except OSError:
            pass

    last_error: Exception | None = None
    for mirror_index, url in enumerate(KARAOKE_FALLBACK_MIRRORS, start=1):
        for attempt in range(1, 4):
            partial = target.with_suffix(target.suffix + ".part")
            try:
                partial.unlink(missing_ok=True)
                report(
                    "Voz avanzada · descargando modelo alternativo estable "
                    f"({mirror_index}/{len(KARAOKE_FALLBACK_MIRRORS)}, intento {attempt}/3)",
                    0.13,
                )
                request = urllib.request.Request(
                    url,
                    headers={"User-Agent": "DrumPracticeStudio/0.1"},
                )
                with urllib.request.urlopen(request, timeout=90) as response, partial.open("wb") as output:
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        output.write(chunk)
                if file_sha256(partial) != KARAOKE_FALLBACK_SHA256:
                    raise RuntimeError("La suma SHA-256 del modelo alternativo no coincide")
                partial.replace(target)
                return target
            except (OSError, RuntimeError, urllib.error.URLError) as error:
                last_error = error
                try:
                    partial.unlink(missing_ok=True)
                except OSError:
                    pass
                if attempt < 3:
                    time.sleep(2.5 * attempt)

    raise RuntimeError(
        "No se pudo descargar el modelo vocal alternativo desde ninguno de los servidores"
        + (f": {last_error}" if last_error is not None else "")
    )


def copy_vocal_results(
    lead_source: Path,
    back_source: Path,
    output_dir: Path,
) -> tuple[Path, Path]:
    lead_target = output_dir / "lead-vocal.wav"
    back_target = output_dir / "back-vocal.wav"
    shutil.copy2(lead_source, lead_target)
    shutil.copy2(back_source, back_target)
    return lead_target, back_target


def split_vocals(vocals_path: Path, output_dir: Path, model_dir: Path) -> tuple[Path, Path]:
    report("Voz avanzada · preparando modelos UVR karaoke", 0.08)
    temp_root = Path(tempfile.mkdtemp(prefix="drumless-vocals-", dir=output_dir))
    errors: list[str] = []
    try:
        for attempt in range(1, KARAOKE_PRESET_RETRIES + 1):
            attempt_dir = temp_root / f"preset-{attempt}"
            try:
                report(
                    f"Voz avanzada · cargando ensemble karaoke (intento {attempt}/{KARAOKE_PRESET_RETRIES})",
                    0.09,
                )
                lead, back = run_vocal_separator(
                    vocals_path,
                    attempt_dir,
                    model_dir,
                    ensemble_preset="karaoke",
                )
                report("Voz avanzada · separando voz principal y coros", 0.18)
                return copy_vocal_results(lead, back, output_dir)
            except Exception as error:
                errors.append(f"ensemble completo: {type(error).__name__}: {error}")
                clean_failed_model_download(model_dir, error)
                shutil.rmtree(attempt_dir, ignore_errors=True)
                if attempt < KARAOKE_PRESET_RETRIES:
                    report(
                        "El servidor del modelo principal no respondió; reintentando automáticamente",
                        0.10,
                    )
                    time.sleep(3.0 * attempt)

        try:
            report(
                "Voz avanzada · usando ensemble karaoke de dos modelos",
                0.11,
            )
            two_model_dir = temp_root / "two-model"
            lead, back = run_vocal_separator(
                vocals_path,
                two_model_dir,
                model_dir,
                model_filenames=KARAOKE_TWO_MODEL_ENSEMBLE,
            )
            report("Voz avanzada · separando voz principal y coros", 0.18)
            return copy_vocal_results(lead, back, output_dir)
        except Exception as error:
            errors.append(f"ensemble de dos modelos: {type(error).__name__}: {error}")
            clean_failed_model_download(model_dir, error)
            shutil.rmtree(temp_root / "two-model", ignore_errors=True)

        report(
            "Los modelos principales no están disponibles; usando el modelo karaoke alternativo",
            0.12,
        )
        ensure_fallback_karaoke_model(model_dir)
        fallback_dir = temp_root / "fallback"
        try:
            lead, back = run_vocal_separator(
                vocals_path,
                fallback_dir,
                model_dir,
                model_filenames=KARAOKE_FALLBACK_MODEL,
            )
            report("Voz avanzada · separando voz principal y coros", 0.18)
            return copy_vocal_results(lead, back, output_dir)
        except Exception as error:
            errors.append(f"modelo alternativo: {type(error).__name__}: {error}")
            raise RuntimeError(" | ".join(errors)) from error
    finally:
        shutil.rmtree(temp_root, ignore_errors=True)


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

    ensure_private_ffmpeg()
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
        traceback.print_exc()
        print(
            f"ERROR: {type(exc).__name__}: {exc}",
            file=sys.stderr,
            flush=True,
        )
        raise SystemExit(1)
