import AVFoundation
import AudioToolbox
import Foundation

setbuf(stdout, nil)

private struct NoteEvent {
    let time: Double
    let note: UInt8
    let velocity: UInt8
    let isNoteOn: Bool
}

private enum InstrumentSource {
    case exs(String)
    case generalMIDI(UInt8)
}

private struct Stem {
    let name: String
    let source: InstrumentSource
    let pan: Float
    let gain: Float
    let notes: [(start: Double, duration: Double, note: UInt8, velocity: UInt8)]
}

private let sampleRate = 48_000.0
private let renderDuration = 5.35
private let maximumFrameCount: AVAudioFrameCount = 4_096
private let dlsPath = "/System/Library/Components/CoreAudio.component/Contents/Resources/gs_instruments.dls"

private func expandedEvents(_ notes: [(start: Double, duration: Double, note: UInt8, velocity: UInt8)]) -> [NoteEvent] {
    notes.flatMap { item in
        [
            NoteEvent(time: item.start, note: item.note, velocity: item.velocity, isNoteOn: true),
            NoteEvent(time: item.start + item.duration, note: item.note, velocity: 0, isNoteOn: false),
        ]
    }
    .sorted {
        if abs($0.time - $1.time) > 0.000_001 { return $0.time < $1.time }
        // Release repeated notes before retriggering them at the same sample.
        return !$0.isNoteOn && $1.isNoteOn
    }
}

private func render(stem: Stem, to outputURL: URL) throws {
    let engine = AVAudioEngine()
    let sampler = AVAudioUnitSampler()
    let trackMixer = AVAudioMixerNode()

    engine.attach(sampler)
    engine.attach(trackMixer)

    let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 2)!
    engine.connect(sampler, to: trackMixer, format: nil)
    engine.connect(trackMixer, to: engine.mainMixerNode, format: nil)
    trackMixer.pan = stem.pan
    trackMixer.outputVolume = stem.gain

    switch stem.source {
    case .exs(let path):
        print("  loading EXS")
        try sampler.loadInstrument(at: URL(fileURLWithPath: path))
    case .generalMIDI(let program):
        print("  loading DLS program \(program)")
        try sampler.loadSoundBankInstrument(
            at: URL(fileURLWithPath: dlsPath),
            program: program,
            bankMSB: UInt8(kAUSampler_DefaultMelodicBankMSB),
            bankLSB: UInt8(kAUSampler_DefaultBankLSB)
        )
    }
    print("  instrument loaded")

    do {
        try engine.enableManualRenderingMode(
            .offline,
            format: format,
            maximumFrameCount: maximumFrameCount
        )
    } catch {
        throw NSError(
            domain: "BartenderSortStemRenderer",
            code: 10,
            userInfo: [NSLocalizedDescriptionKey: "Could not enable manual render for \(stem.name): \(error)"]
        )
    }
    print("  manual rendering enabled")

    let outputSettings: [String: Any] = [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVSampleRateKey: sampleRate,
        AVNumberOfChannelsKey: 2,
        AVLinearPCMBitDepthKey: 32,
        AVLinearPCMIsFloatKey: true,
        AVLinearPCMIsBigEndianKey: false,
        AVLinearPCMIsNonInterleaved: false,
    ]
    let outputFile = try AVAudioFile(forWriting: outputURL, settings: outputSettings)
    print("  output file opened")
    let renderBuffer = AVAudioPCMBuffer(
        pcmFormat: engine.manualRenderingFormat,
        frameCapacity: maximumFrameCount
    )!

    let events = expandedEvents(stem.notes)
    var eventIndex = 0
    let totalFrames = AVAudioFramePosition((renderDuration * sampleRate).rounded())

    print("  starting engine")
    try engine.start()
    print("  engine started")
    defer { engine.stop() }

    while engine.manualRenderingSampleTime < totalFrames {
        let currentFrame = engine.manualRenderingSampleTime

        while eventIndex < events.count {
            let eventFrame = AVAudioFramePosition((events[eventIndex].time * sampleRate).rounded())
            if eventFrame > currentFrame { break }

            let event = events[eventIndex]
            if event.isNoteOn {
                sampler.startNote(event.note, withVelocity: event.velocity, onChannel: 0)
            } else {
                sampler.stopNote(event.note, onChannel: 0)
            }
            eventIndex += 1
        }

        let nextEventFrame: AVAudioFramePosition = {
            guard eventIndex < events.count else { return totalFrames }
            return AVAudioFramePosition((events[eventIndex].time * sampleRate).rounded())
        }()

        let untilEvent = max(1, nextEventFrame - currentFrame)
        let remaining = totalFrames - currentFrame
        let requested = AVAudioFrameCount(min(
            AVAudioFramePosition(maximumFrameCount),
            min(untilEvent, remaining)
        ))

        let status = try engine.renderOffline(requested, to: renderBuffer)
        switch status {
        case .success:
            try outputFile.write(from: renderBuffer)
        case .insufficientDataFromInputNode:
            continue
        case .cannotDoInCurrentContext:
            continue
        case .error:
            throw NSError(
                domain: "BartenderSortStemRenderer",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "Offline render failed for \(stem.name)"]
            )
        @unknown default:
            throw NSError(
                domain: "BartenderSortStemRenderer",
                code: 3,
                userInfo: [NSLocalizedDescriptionKey: "Unknown render status for \(stem.name)"]
            )
        }
    }
}

private let stems: [Stem] = [
    Stem(
        name: "piano",
        source: .generalMIDI(0),
        pan: -0.08,
        gain: 0.72,
        notes: [
            (0.12, 0.64, 49, 31), (0.12, 0.64, 56, 27), (0.12, 0.64, 58, 29),
            (0.12, 0.64, 63, 32), (0.12, 0.64, 65, 35),
            (0.82, 0.42, 49, 27), (0.82, 0.42, 54, 30), (0.82, 0.42, 58, 28),
            (0.82, 0.42, 60, 31), (0.82, 0.42, 65, 29),
            (1.28, 0.38, 46, 30), (1.28, 0.38, 53, 28), (1.28, 0.38, 56, 31),
            (1.28, 0.38, 61, 32), (1.28, 0.38, 63, 29),
            (1.78, 0.47, 44, 34), (1.78, 0.47, 54, 31), (1.78, 0.47, 58, 33),
            (1.78, 0.47, 61, 32), (1.78, 0.47, 66, 35),

            (2.495, 0.78, 49, 79), (2.495, 0.78, 56, 66), (2.495, 0.78, 58, 71),
            (2.495, 0.78, 63, 72), (2.495, 0.78, 65, 83),
            (3.08, 0.47, 42, 56), (3.08, 0.47, 49, 52), (3.08, 0.47, 53, 56),
            (3.08, 0.47, 56, 55), (3.08, 0.47, 60, 58),
            (3.48, 0.49, 44, 61), (3.48, 0.49, 54, 57), (3.48, 0.49, 58, 58),
            (3.48, 0.49, 61, 61), (3.48, 0.49, 66, 59),
            (3.93, 1.08, 49, 68), (3.93, 1.08, 56, 57), (3.93, 1.08, 58, 60),
            (3.93, 1.08, 63, 61), (3.93, 1.08, 65, 72),
        ]
    ),
    Stem(
        name: "upright_bass",
        source: .generalMIDI(32),
        pan: 0.03,
        gain: 0.84,
        notes: [
            (1.28, 0.34, 34, 47),
            (1.78, 0.40, 32, 52),
            (2.495, 0.49, 37, 97),
            (3.08, 0.34, 42, 72),
            (3.48, 0.34, 44, 79),
            (3.93, 0.78, 37, 88),
        ]
    ),
    Stem(
        name: "strings",
        source: .generalMIDI(48),
        pan: 0.10,
        gain: 0.34,
        notes: [
            (2.48, 0.73, 56, 42), (2.48, 0.73, 58, 37), (2.48, 0.73, 63, 39), (2.48, 0.73, 65, 46),
            (3.07, 0.45, 54, 35), (3.07, 0.45, 58, 34), (3.07, 0.45, 60, 33), (3.07, 0.45, 65, 38),
            (3.47, 0.47, 54, 38), (3.47, 0.47, 58, 36), (3.47, 0.47, 61, 37), (3.47, 0.47, 66, 41),
            (3.92, 1.14, 56, 39), (3.92, 1.14, 58, 36), (3.92, 1.14, 63, 38), (3.92, 1.14, 65, 45),
        ]
    ),
    Stem(
        name: "vibraphone",
        source: .generalMIDI(11),
        pan: 0.04,
        gain: 0.70,
        notes: [
            (0.145, 0.50, 77, 70),
            (0.395, 0.54, 82, 76),
            (0.645, 0.62, 87, 82),
            (0.895, 0.46, 84, 73),
            (1.145, 0.70, 89, 90),

            (2.50, 0.54, 85, 104),
            (2.98, 0.16, 80, 67),
            (3.17, 0.16, 82, 73),
            (3.38, 0.27, 89, 91),
            (3.70, 0.21, 87, 80),
            (3.95, 0.78, 85, 88),
        ]
    ),
    Stem(
        name: "pizzicato",
        source: .generalMIDI(45),
        pan: 0.0,
        gain: 0.64,
        notes: [
            (1.30, 0.15, 68, 52),
            (1.55, 0.15, 72, 57),
            (1.80, 0.14, 75, 64),
            (2.01, 0.13, 78, 70),
            (2.18, 0.12, 77, 78),
            (2.32, 0.10, 82, 89),
            (2.42, 0.06, 84, 102),
        ]
    ),
    Stem(
        name: "muted_trumpet",
        source: .generalMIDI(59),
        pan: 0.13,
        gain: 0.46,
        notes: [
            (2.50, 0.43, 73, 84),
            (2.98, 0.15, 68, 58),
            (3.17, 0.15, 70, 62),
            (3.38, 0.26, 77, 73),
            (3.70, 0.20, 75, 67),
            (3.95, 0.68, 73, 75),
        ]
    ),
]

guard CommandLine.arguments.count == 2 else {
    fputs("Usage: RenderBartenderSortStems <output-directory>\n", stderr)
    exit(64)
}

let outputDirectory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

for stem in stems {
    let outputURL = outputDirectory.appendingPathComponent("\(stem.name).wav")
    print("Rendering \(stem.name)...")
    try render(stem: stem, to: outputURL)
}

print("Rendered \(stems.count) stems to \(outputDirectory.path)")
