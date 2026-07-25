"""Compatibilidad de argumentos para los procesos Python lanzados por Drumless.

Algunas versiones de Demucs declaran --segment como entero, mientras que otras
aceptan el valor decimal recomendado por el modelo htdemucs. Drumless puede
convivir con ambas normalizando únicamente ese argumento antes de que argparse
procese la línea de comandos.
"""

from __future__ import annotations

import math
import sys


def _normalize_demucs_segment() -> None:
    try:
        index = sys.argv.index("--segment")
    except ValueError:
        return

    value_index = index + 1
    if value_index >= len(sys.argv):
        return

    raw_value = sys.argv[value_index]
    try:
        numeric_value = float(raw_value)
    except (TypeError, ValueError):
        return

    if not math.isfinite(numeric_value):
        return

    # Las versiones cuyo argparse usa type=int rechazan "7.8". Siete segundos
    # conserva el procesamiento por bloques y respeta el límite del modelo.
    if not numeric_value.is_integer():
        sys.argv[value_index] = str(max(1, math.floor(numeric_value)))


_normalize_demucs_segment()
