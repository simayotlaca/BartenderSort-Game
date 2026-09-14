#!/usr/bin/env python3
"""I compose and master the win sting here. Apple General MIDI supplies dry instruments; I synthesize the glass, impacts, transitions, rhythm and effects without source music."""

from __future__ import annotations

import argparse
import math
from pathlib import Path

import numpy as np
import soundfile as sf


SR = 48_000
DURATION = 5.35
FRAMES = int(round(SR * DURATION))
CHEERS_HIT_SECONDS = 2.5
RNG = np.random.default_rng(0xC0C7A11)


def midi_hz(note: float) -> float:
    return 440.0 * (2.0 ** ((note - 69.0) / 12.0))


def equal_power_pan(pan: float) -> tuple[float, float]:
    pan = float(np.clip(pan, -1.0, 1.0))
    angle = (pan + 1.0) * math.pi / 4.0
    return math.cos(angle), math.sin(angle)


def add_mono(bus: np.ndarray, signal: np.ndarray, start: float, gain: float = 1.0, pan: float = 0.0) -> None:
    first = max(0, int(round(start * SR)))
    if first >= len(bus):
        return
    count = min(len(signal), len(bus) - first)
    left, right = equal_power_pan(pan)
    bus[first:first + count, 0] += signal[:count] * gain * left
    bus[first:first + count, 1] += signal[:count] * gain * right


def add_stereo(bus: np.ndarray, signal: np.ndarray, gain: float = 1.0) -> None:
    count = min(len(bus), len(signal))
    bus[:count] += signal[:count] * gain


def normalize_sound(signal: np.ndarray, peak: float = 1.0) -> np.ndarray:
    maximum = float(np.max(np.abs(signal)))
    if maximum <= 1e-12:
        return signal
    return signal * (peak / maximum)


def smooth_tail(signal: np.ndarray, seconds: float) -> np.ndarray:
    fade_samples = min(len(signal), max(2, int(round(seconds * SR))))
    output = signal.copy()
    curve = np.cos(np.linspace(0.0, math.pi / 2.0, fade_samples)) ** 2
    output[-fade_samples:] *= curve
    return output


def band_limited_noise(duration: float, low_hz: float, high_hz: float, seed: int) -> np.ndarray:
    count = max(8, int(round(duration * SR)))
    generator = np.random.default_rng(seed)
    noise = generator.standard_normal(count)
    spectrum = np.fft.rfft(noise)
    frequencies = np.fft.rfftfreq(count, 1.0 / SR)

    low_ramp = 1.0 / (1.0 + np.exp(-(frequencies - low_hz) / max(30.0, low_hz * 0.12)))
    high_ramp = 1.0 / (1.0 + np.exp((frequencies - high_hz) / max(80.0, high_hz * 0.07)))
    spectrum *= low_ramp * high_ramp
    colored = np.fft.irfft(spectrum, n=count)
    return normalize_sound(colored)


def plucked_string(note: int, duration: float, seed: int, brightness: float = 0.72) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    frequency = midi_hz(note)
    generator = np.random.default_rng(seed)
    signal = np.zeros(count, dtype=np.float64)
    pluck_position = 0.217

    highest_harmonic = min(24, int((SR * 0.46) / frequency))
    for harmonic in range(1, highest_harmonic + 1):
        spatial_notch = abs(math.sin(math.pi * harmonic * pluck_position))
        amplitude = spatial_notch / (harmonic ** (1.17 + (1.0 - brightness) * 0.75))
        decay = (0.92 + duration * 0.20) / (1.0 + 0.105 * (harmonic ** 1.23))
        detune = 1.0 + generator.normal(0.0, 0.00018)
        phase = generator.uniform(-0.12, 0.12)
        signal += amplitude * np.sin(2.0 * math.pi * frequency * harmonic * detune * time + phase) * np.exp(-time / decay)

    attack = 1.0 - np.exp(-time / 0.0011)
    body = (
        0.07 * np.sin(2.0 * math.pi * 187.0 * time)
        + 0.035 * np.sin(2.0 * math.pi * 423.0 * time + 0.8)
    ) * np.exp(-time / 0.17)
    pick = band_limited_noise(duration, 2_400.0, 11_000.0, seed + 7) * np.exp(-time / 0.007)
    signal = signal * attack + body + 0.055 * pick
    return smooth_tail(normalize_sound(signal), min(0.10, duration * 0.18))


def crystal_note(note: int, duration: float, seed: int) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    base = midi_hz(note)
    generator = np.random.default_rng(seed)
    ratios = [1.0, 2.071, 3.918, 5.624, 7.891, 10.54]
    amplitudes = [0.50, 0.34, 0.23, 0.14, 0.085, 0.050]
    decays = [0.52, 0.39, 0.30, 0.22, 0.16, 0.12]
    signal = np.zeros(count, dtype=np.float64)
    for ratio, amplitude, decay in zip(ratios, amplitudes, decays):
        frequency = base * ratio * (1.0 + generator.normal(0.0, 0.00045))
        if frequency >= SR * 0.47:
            continue
        signal += amplitude * np.sin(2.0 * math.pi * frequency * time) * np.exp(-time / decay)
    tap = band_limited_noise(duration, 3_500.0, 16_500.0, seed + 23) * np.exp(-time / 0.0045)
    return smooth_tail(normalize_sound(signal + 0.15 * tap), min(0.075, duration * 0.17))


def glass_clink(duration: float, seed: int, pitch_scale: float = 1.0) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    generator = np.random.default_rng(seed)
    frequencies = np.array([1_104.0, 1_657.0, 2_741.0, 3_936.0, 5_762.0, 8_137.0, 11_346.0, 15_220.0]) * pitch_scale
    amplitudes = np.array([0.17, 0.25, 0.42, 0.38, 0.29, 0.20, 0.12, 0.06])
    decays = np.array([0.40, 0.31, 0.27, 0.22, 0.17, 0.13, 0.095, 0.060])
    signal = np.zeros(count, dtype=np.float64)
    for frequency, amplitude, decay in zip(frequencies, amplitudes, decays):
        if frequency >= SR * 0.47:
            continue
        frequency *= 1.0 + generator.normal(0.0, 0.0007)
        phase = generator.uniform(-0.07, 0.07)
        signal += amplitude * np.sin(2.0 * math.pi * frequency * time + phase) * np.exp(-time / decay)

    transient = band_limited_noise(duration, 1_800.0, 18_500.0, seed + 101)
    transient *= np.exp(-time / 0.0032)
    micro_reflection = np.zeros_like(signal)
    reflection_delay = int(round(0.0115 * SR))
    micro_reflection[reflection_delay:] = signal[:-reflection_delay] * 0.16
    combined = normalize_sound(signal + 0.24 * transient + micro_reflection)
    return smooth_tail(combined, min(0.09, duration * 0.14))


def soft_kick(duration: float = 0.32) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    frequency = 52.0 + 74.0 * np.exp(-time / 0.032)
    phase = 2.0 * math.pi * np.cumsum(frequency) / SR
    envelope = (1.0 - np.exp(-time / 0.0025)) * np.exp(-time / 0.105)
    click = band_limited_noise(duration, 900.0, 3_500.0, 39) * np.exp(-time / 0.008)
    return smooth_tail(normalize_sound(np.sin(phase) * envelope + 0.045 * click), 0.035)


def brushed_tick(duration: float, seed: int) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    noise = band_limited_noise(duration, 2_200.0, 13_500.0, seed)
    envelope = (1.0 - np.exp(-time / 0.001)) * np.exp(-time / 0.025)
    wood = np.sin(2.0 * math.pi * 1_370.0 * time) * np.exp(-time / 0.018)
    return smooth_tail(normalize_sound(noise * envelope + 0.22 * wood), 0.025)


def reverse_whoosh(duration: float, seed: int) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    noise = band_limited_noise(duration, 650.0, 14_000.0, seed)
    ramp = np.sin(np.linspace(0.0, math.pi / 2.0, count)) ** 2.4
    flutter = 0.86 + 0.14 * np.sin(2.0 * math.pi * (6.0 * time + 8.0 * time * time))
    chirp_phase = 2.0 * math.pi * (520.0 * time + 1_600.0 * time * time)
    return normalize_sound(noise * ramp * flutter + 0.06 * np.sin(chirp_phase) * ramp)


def fft_convolve(signal: np.ndarray, impulse: np.ndarray, output_length: int) -> np.ndarray:
    convolution_length = len(signal) + len(impulse) - 1
    fft_size = 1 << (convolution_length - 1).bit_length()
    result = np.fft.irfft(np.fft.rfft(signal, fft_size) * np.fft.rfft(impulse, fft_size), fft_size)
    return result[:output_length]


def reverb_ir(duration: float, decay: float, seed: int, brightness: float) -> np.ndarray:
    count = int(round(duration * SR))
    time = np.arange(count, dtype=np.float64) / SR
    generator = np.random.default_rng(seed)
    raw = generator.standard_normal(count)
    spectrum = np.fft.rfft(raw)
    frequencies = np.fft.rfftfreq(count, 1.0 / SR)
    spectrum *= 1.0 / np.sqrt(1.0 + (frequencies / (2_400.0 + brightness * 5_500.0)) ** 2)
    tail = np.fft.irfft(spectrum, n=count)
    tail *= np.exp(-time / decay)
    tail[: int(0.019 * SR)] = 0.0

    impulse = tail
    for milliseconds, level in [(19, 0.45), (31, -0.31), (47, 0.27), (71, -0.21), (103, 0.16)]:
        index = int(round(milliseconds * 0.001 * SR))
        if index < count:
            impulse[index] += level

    energy = math.sqrt(float(np.sum(impulse * impulse)))
    return impulse * (0.72 / max(energy, 1e-12))


def stereo_reverb(send: np.ndarray, duration: float, decay: float, wet: float, seed: int, brightness: float) -> np.ndarray:
    mid = (send[:, 0] + send[:, 1]) * 0.5
    side = (send[:, 0] - send[:, 1]) * 0.5
    left_ir = reverb_ir(duration, decay, seed, brightness)
    right_ir = reverb_ir(duration, decay * 1.035, seed + 1, brightness * 0.96)
    left = fft_convolve(mid, left_ir, FRAMES) + 0.22 * fft_convolve(side, right_ir, FRAMES)
    right = fft_convolve(mid, right_ir, FRAMES) - 0.22 * fft_convolve(side, left_ir, FRAMES)
    return np.column_stack((left, right)) * wet


def allpass_filter(signal: np.ndarray, delay_samples: int, feedback: float) -> np.ndarray:
    output = np.zeros_like(signal)
    for index in range(len(signal)):
        delayed_input = signal[index - delay_samples] if index >= delay_samples else 0.0
        delayed_output = output[index - delay_samples] if index >= delay_samples else 0.0
        output[index] = -feedback * signal[index] + delayed_input + feedback * delayed_output
    return output


def highpass_first_order(signal: np.ndarray, cutoff_hz: float) -> np.ndarray:
    output = np.zeros_like(signal)
    alpha = math.exp(-2.0 * math.pi * cutoff_hz / SR)
    previous_input = 0.0
    previous_output = 0.0
    for index, value in enumerate(signal):
        current = alpha * (previous_output + value - previous_input)
        output[index] = current
        previous_input = value
        previous_output = current
    return output


def add_decorrelated_width(signal: np.ndarray) -> np.ndarray:
    mid = (signal[:, 0] + signal[:, 1]) * 0.5
    path_a = allpass_filter(mid, 347, 0.61)
    path_b = allpass_filter(mid, 521, 0.53)
    decorrelated = highpass_first_order(path_a - path_b, 240.0)

    amount = np.full(FRAMES, 0.15, dtype=np.float64)
    approach = int(1.22 * SR)
    hit = int(CHEERS_HIT_SECONDS * SR)
    opened = int(2.82 * SR)
    amount[approach:hit] = np.linspace(0.15, 0.09, hit - approach, endpoint=False)
    amount[hit:opened] = np.linspace(0.34, 0.27, opened - hit, endpoint=False)
    amount[opened:] = 0.27
    amount[-int(0.72 * SR):] *= np.linspace(1.0, 0.0, int(0.72 * SR))

    side = decorrelated * amount
    widened = signal.copy()
    widened[:, 0] += side
    widened[:, 1] -= side
    return widened


def spectral_master_eq(signal: np.ndarray) -> np.ndarray:
    output = np.zeros_like(signal)
    frequencies = np.fft.rfftfreq(len(signal), 1.0 / SR)
    highpass = 1.0 / np.sqrt(1.0 + (34.0 / np.maximum(frequencies, 0.05)) ** 8)
    low_cleanup_db = -1.1 * np.exp(-0.5 * (np.log2(np.maximum(frequencies, 20.0) / 285.0) / 0.78) ** 2)
    nasal_db = -1.35 * np.exp(-0.5 * (np.log2(np.maximum(frequencies, 20.0) / 1_280.0) / 0.68) ** 2)
    presence_db = 1.15 * np.exp(-0.5 * (np.log2(np.maximum(frequencies, 20.0) / 4_800.0) / 0.88) ** 2)
    air_db = 1.05 / (1.0 + np.exp(-(frequencies - 8_300.0) / 1_650.0))
    response = highpass * (10.0 ** ((low_cleanup_db + nasal_db + presence_db + air_db) / 20.0))
    for channel in range(2):
        output[:, channel] = np.fft.irfft(np.fft.rfft(signal[:, channel]) * response, n=len(signal))
    return output


def linked_compressor(signal: np.ndarray, threshold: float = 0.20, ratio: float = 2.4) -> np.ndarray:
    detector = np.max(np.abs(signal), axis=1)
    envelope = np.empty_like(detector)
    attack = math.exp(-1.0 / (0.006 * SR))
    release = math.exp(-1.0 / (0.115 * SR))
    level = 0.0
    for index, value in enumerate(detector):
        coefficient = attack if value > level else release
        level = coefficient * level + (1.0 - coefficient) * value
        envelope[index] = level

    safe = np.maximum(envelope, 1e-9)
    over = np.maximum(0.0, 20.0 * np.log10(safe / threshold))
    reduction_db = -over * (1.0 - 1.0 / ratio)
    target_gain = 10.0 ** (reduction_db / 20.0)

    smoothed = np.empty_like(target_gain)
    coefficient = math.exp(-1.0 / (0.020 * SR))
    gain = 1.0
    for index, value in enumerate(target_gain):
        gain = coefficient * gain + (1.0 - coefficient) * value
        smoothed[index] = gain
    return signal * smoothed[:, None]


def load_stem(stems_directory: Path, filename: str) -> np.ndarray:
    data, sample_rate = sf.read(stems_directory / filename, always_2d=True, dtype="float64")
    if sample_rate != SR:
        raise ValueError(f"Unexpected sample rate for {filename}: {sample_rate}")
    if data.shape[1] != 2:
        raise ValueError(f"Expected a stereo stem: {filename}")
    if len(data) < FRAMES:
        data = np.pad(data, ((0, FRAMES - len(data)), (0, 0)))
    return data[:FRAMES]


def compose(stems_directory: Path) -> np.ndarray:
    dry = np.zeros((FRAMES, 2), dtype=np.float64)
    room_send = np.zeros_like(dry)
    plate_send = np.zeros_like(dry)

    piano = load_stem(stems_directory, "piano.wav")
    bass = load_stem(stems_directory, "upright_bass.wav")
    strings = load_stem(stems_directory, "strings.wav")
    vibe = load_stem(stems_directory, "vibraphone.wav")
    pizzicato = load_stem(stems_directory, "pizzicato.wav")
    trumpet = load_stem(stems_directory, "muted_trumpet.wav")

    add_stereo(dry, piano, 1.25)
    add_stereo(dry, bass, 0.95)
    add_stereo(dry, strings, 0.85)
    add_stereo(dry, pizzicato, 0.72)
    add_stereo(dry, trumpet, 1.72)
    # I keep the vibraphone quiet as a light background shimmer.
    add_stereo(dry, vibe, 0.13)

    add_stereo(room_send, piano, 0.60)
    add_stereo(room_send, pizzicato, 0.48)
    add_stereo(room_send, trumpet, 0.44)
    add_stereo(plate_send, strings, 0.56)
    add_stereo(plate_send, trumpet, 0.62)
    add_stereo(plate_send, vibe, 0.16)

    intro_notes = [
        (0.145, 77, 0.82, -0.24, 0.29),
        (0.395, 82, 0.86, 0.16, 0.32),
        (0.645, 87, 0.92, -0.10, 0.35),
        (0.895, 84, 0.72, 0.20, 0.28),
        (1.145, 89, 1.05, 0.00, 0.39),
    ]
    for index, (start, note, duration, pan, gain) in enumerate(intro_notes):
        sound = plucked_string(note, duration, 1_000 + index, brightness=0.73)
        add_mono(dry, sound, start, gain=gain, pan=pan)
        add_mono(room_send, sound, start, gain=gain * 0.42, pan=pan)

    approach_notes = [
        (1.30, 68, -0.72, 0.17),
        (1.55, 72, 0.72, 0.18),
        (1.80, 75, -0.54, 0.20),
        (2.01, 78, 0.54, 0.22),
        (2.18, 77, -0.34, 0.25),
        (2.32, 82, 0.34, 0.28),
        (2.42, 84, 0.08, 0.31),
    ]
    for index, (start, note, pan, gain) in enumerate(approach_notes):
        sound = crystal_note(note, 0.66, 2_000 + index)
        add_mono(dry, sound, start, gain=gain, pan=pan)
        add_mono(plate_send, sound, start, gain=gain * 0.52, pan=pan)

    fanfare_notes = [
        (2.50, 85, 0.72, -0.07, 0.32),
        (2.98, 80, 0.38, 0.14, 0.23),
        (3.17, 82, 0.38, -0.12, 0.25),
        (3.38, 89, 0.58, 0.10, 0.30),
        (3.70, 87, 0.52, -0.08, 0.27),
        (3.95, 85, 1.22, 0.00, 0.31),
    ]
    for index, (start, note, duration, pan, gain) in enumerate(fanfare_notes):
        sound = plucked_string(note, duration, 3_000 + index, brightness=0.61)
        add_mono(dry, sound, start, gain=gain, pan=pan)
        add_mono(plate_send, sound, start, gain=gain * 0.48, pan=pan)

    # A reverse shimmer follows the text reveal.
    shimmer = reverse_whoosh(0.21, 4_001)
    add_mono(dry, shimmer, 0.0, gain=0.055, pan=-0.15)
    add_mono(room_send, shimmer, 0.0, gain=0.08, pan=0.22)

    # I leave a short centred gap as the glasses meet.
    whoosh = reverse_whoosh(0.43, 4_101)
    add_mono(dry, whoosh, 2.06, gain=0.070, pan=0.0)

    clink_left = glass_clink(0.88, 5_001, pitch_scale=0.994)
    clink_right = glass_clink(0.86, 5_002, pitch_scale=1.011)
    add_mono(dry, clink_left, CHEERS_HIT_SECONDS, gain=0.42, pan=-0.11)
    add_mono(dry, clink_right, CHEERS_HIT_SECONDS + 0.010, gain=0.36, pan=0.11)
    add_mono(plate_send, clink_left, CHEERS_HIT_SECONDS, gain=0.62, pan=-0.16)
    add_mono(plate_send, clink_right, CHEERS_HIT_SECONDS + 0.010, gain=0.55, pan=0.16)

    kick = soft_kick()
    for start, gain in [(2.495, 0.34), (3.08, 0.14), (3.48, 0.16), (3.93, 0.21)]:
        add_mono(dry, kick, start, gain=gain, pan=0.0)

    brush_times = [0.82, 1.28, 1.78, 2.18, 2.495, 2.86, 3.28, 3.68, 4.10]
    for index, start in enumerate(brush_times):
        tick = brushed_tick(0.12, 6_000 + index)
        pan = -0.22 if index % 2 == 0 else 0.22
        gain = 0.035 if start < 2.4 else 0.050
        add_mono(dry, tick, start, gain=gain, pan=pan)
        add_mono(room_send, tick, start, gain=gain * 0.35, pan=pan)

    dry += stereo_reverb(room_send, duration=0.46, decay=0.105, wet=0.22, seed=7_001, brightness=0.45)
    dry += stereo_reverb(plate_send, duration=1.18, decay=0.31, wet=0.31, seed=7_101, brightness=0.86)

    # I narrow stereo as the glasses approach, then widen it on impact.
    width = np.ones(FRAMES, dtype=np.float64)
    pre_start = int(1.22 * SR)
    hit = int(CHEERS_HIT_SECONDS * SR)
    post = int(2.76 * SR)
    width[:pre_start] = 0.86
    width[pre_start:hit] = np.linspace(0.86, 0.62, hit - pre_start, endpoint=False)
    width[hit:post] = np.linspace(1.18, 1.08, post - hit, endpoint=False)
    width[post:] = 1.08
    mid = (dry[:, 0] + dry[:, 1]) * 0.5
    side = (dry[:, 0] - dry[:, 1]) * 0.5 * width
    mix = np.column_stack((mid + side, mid - side))
    mix = add_decorrelated_width(mix)

    mix = spectral_master_eq(mix)
    active = mix[int(0.08 * SR): int(4.75 * SR)]
    active_rms = math.sqrt(float(np.mean(active * active)))
    mix *= 0.135 / max(active_rms, 1e-9)
    mix = linked_compressor(mix)

    # I add gentle saturation and leave fixed true-peak headroom.
    drive = 1.12
    mix = np.tanh(mix * drive) / math.tanh(drive)
    maximum = float(np.max(np.abs(mix)))
    mix *= (10.0 ** (-1.6 / 20.0)) / max(maximum, 1e-9)

    fade_in = int(0.004 * SR)
    fade_in_curve = (np.sin(np.linspace(0.0, math.pi / 2.0, fade_in)) ** 2)[:, None]
    mix[:fade_in] *= fade_in_curve
    fade_out = int(0.28 * SR)
    fade_curve = (np.cos(np.linspace(0.0, math.pi / 2.0, fade_out)) ** 2)[:, None]
    mix[-fade_out:] *= fade_curve
    return mix


def write_outputs(mix: np.ndarray, master_directory: Path, asset_directory: Path) -> None:
    master_directory.mkdir(parents=True, exist_ok=True)
    asset_directory.mkdir(parents=True, exist_ok=True)
    wav_path = master_directory / "BartenderSort_CrystalCheers_Win_v1.wav"
    ogg_path = asset_directory / "BartenderSort_CrystalCheers_Win_v1.ogg"

    dither = (RNG.random(mix.shape) - RNG.random(mix.shape)) / (2.0 ** 24)
    sf.write(wav_path, np.clip(mix + dither, -1.0, 1.0), SR, subtype="PCM_24")
    sf.write(
        ogg_path,
        np.clip(mix, -1.0, 1.0),
        SR,
        format="OGG",
        subtype="VORBIS",
        compression_level=0.0,
    )

    peak_dbfs = 20.0 * math.log10(max(float(np.max(np.abs(mix))), 1e-12))
    total_rms_dbfs = 20.0 * math.log10(max(math.sqrt(float(np.mean(mix * mix))), 1e-12))
    active = mix[int(0.08 * SR): int(4.75 * SR)]
    active_rms_dbfs = 20.0 * math.log10(max(math.sqrt(float(np.mean(active * active))), 1e-12))
    correlation = float(np.corrcoef(mix[:, 0], mix[:, 1])[0, 1])
    print(f"WAV: {wav_path}")
    print(f"OGG: {ogg_path}")
    print(f"duration={DURATION:.3f}s cheers_hit={CHEERS_HIT_SECONDS:.3f}s")
    print(f"peak={peak_dbfs:.2f}dBFS active_rms={active_rms_dbfs:.2f}dBFS total_rms={total_rms_dbfs:.2f}dBFS")
    print(f"stereo_correlation={correlation:.3f}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("stems_directory", type=Path)
    parser.add_argument("master_directory", type=Path)
    parser.add_argument("asset_directory", type=Path)
    arguments = parser.parse_args()
    mix = compose(arguments.stems_directory)
    write_outputs(mix, arguments.master_directory, arguments.asset_directory)


if __name__ == "__main__":
    main()
