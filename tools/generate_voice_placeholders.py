#!/usr/bin/env python3
"""
generate_voice_placeholders.py — placeholder syllable banks for the
negotiation portrait voice.

The portrait (Scripts/Systems/Negotiation/NegotiationPortrait.cs) plays one
short clip per mouth flap from
    Assets/Audio/Voice/Negotiation/<archetype>/*.wav|*.ogg
and is SILENT when that folder is empty. This script fills the folders with
soft, formant-shaped vowel mumbles (Animal-Crossing style) so the voice can
be heard and judged before real recordings exist. Nothing here is final art.

Run from the project root:
    python3 tools/generate_voice_placeholders.py            # write all six banks
    python3 tools/generate_voice_placeholders.py --clean    # list what it would remove
    python3 tools/generate_voice_placeholders.py --only scholar --clips 8

Delete the generated folders to silence the portrait again. Godot picks the
new .wav files up on its next import scan.

Clip design (per archetype): fundamental f0 with a 14-harmonic series,
shaped by two vowel formants (F1/F2) with Gaussian bandwidths, 8 ms attack,
raised-cosine decay, 60–95 ms total, peak −14 dBFS, 22050 Hz mono 16-bit.
No square or saw waves; nothing above ~4 kHz has meaningful energy.
"""
import argparse
import math
import os
import sys
import wave

try:
    import numpy as np
except ImportError:  # pragma: no cover
    print("numpy is required: pip install numpy", file=sys.stderr)
    sys.exit(1)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "Assets", "Audio", "Voice", "Negotiation")
RATE = 22050

# f0 in Hz, breathiness 0..1, and a per-voice brightness (harmonic rolloff).
VOICES = {
    "merchant":    dict(f0=150.0, breath=0.10, bright=0.85),
    "commander":   dict(f0=105.0, breath=0.05, bright=0.70),
    "scholar":     dict(f0=175.0, breath=0.18, bright=1.00),
    "opportunist": dict(f0=205.0, breath=0.12, bright=1.10),
    "idealist":    dict(f0=225.0, breath=0.22, bright=0.95),
    "survivor":    dict(f0=125.0, breath=0.30, bright=0.60),
}

# Rough adult vowel formants (F1, F2) in Hz.
VOWELS = {
    "a": (730, 1090),
    "e": (530, 1840),
    "i": (390, 1990),
    "o": (570, 840),
    "u": (440, 1020),
    "uh": (640, 1190),
}


def formant_gain(freq, f1, f2):
    """Two Gaussian formant peaks plus a gentle low shelf."""
    g1 = math.exp(-((freq - f1) / 110.0) ** 2)
    g2 = 0.6 * math.exp(-((freq - f2) / 160.0) ** 2)
    shelf = 0.15 * math.exp(-freq / 900.0)
    return g1 + g2 + shelf


def clip(voice, vowel, seed):
    rng = np.random.default_rng(seed)
    f0 = voice["f0"] * (0.95 + rng.random() * 0.10)
    dur = 0.060 + rng.random() * 0.035
    n = int(RATE * dur)
    t = np.arange(n) / RATE
    # Slight downward pitch glide reads as a spoken syllable, not a beep.
    glide = np.linspace(1.03, 0.97, n)
    phase = 2 * math.pi * np.cumsum(f0 * glide) / RATE

    f1, f2 = VOWELS[vowel]
    sig = np.zeros(n)
    for h in range(1, 15):
        fh = f0 * h
        if fh > 4200:
            break
        amp = formant_gain(fh, f1, f2) * (voice["bright"] ** (h - 1)) / h ** 0.35
        sig += amp * np.sin(phase * h)

    if voice["breath"] > 0:
        noise = rng.standard_normal(n)
        # Crude band-limit: moving average, three taps.
        noise = np.convolve(noise, np.ones(3) / 3, mode="same")
        sig += voice["breath"] * 0.25 * noise

    attack = int(RATE * 0.008)
    env = np.ones(n)
    env[:attack] = np.linspace(0, 1, attack)
    decay_start = int(n * 0.35)
    tail = n - decay_start
    env[decay_start:] = 0.5 * (1 + np.cos(np.linspace(0, math.pi, tail)))
    sig *= env

    peak = np.max(np.abs(sig)) or 1.0
    sig = sig / peak * (10 ** (-14 / 20))  # −14 dBFS
    return (sig * 32767).astype(np.int16)


def write_wav(path, data):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data.tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", help="one archetype folder to (re)generate")
    ap.add_argument("--clips", type=int, default=6, help="clips per archetype")
    ap.add_argument("--clean", action="store_true",
                    help="list generated files (this script never deletes)")
    args = ap.parse_args()

    names = [args.only] if args.only else list(VOICES)
    for name in names:
        if name not in VOICES:
            print(f"unknown archetype {name!r}; choose from {', '.join(VOICES)}", file=sys.stderr)
            return 2
        folder = os.path.join(OUT_DIR, name)
        if args.clean:
            if os.path.isdir(folder):
                for f in sorted(os.listdir(folder)):
                    if f.startswith("syl_") and f.endswith(".wav"):
                        print(os.path.join(folder, f))
            continue
        os.makedirs(folder, exist_ok=True)
        vowels = list(VOWELS)
        for i in range(args.clips):
            vowel = vowels[i % len(vowels)]
            data = clip(VOICES[name], vowel, seed=sum(ord(c) for c in name) * 131 + i)
            path = os.path.join(folder, f"syl_{i + 1:02d}_{vowel}.wav")
            write_wav(path, data)
        print(f"{name:12} {args.clips} clips -> {os.path.relpath(folder, ROOT)}")
    if not args.clean:
        print("Portrait voice is now audible. Delete Assets/Audio/Voice/Negotiation/<archetype>/ to silence it.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
