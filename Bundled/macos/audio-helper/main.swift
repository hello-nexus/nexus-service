// nexus-audio-helper
//
// Captures system audio output via ScreenCaptureKit and streams interleaved float32
// stereo PCM at 44100 Hz to stdout. The Nexus service spawns this helper, reads its
// stdout, and feeds the samples into AudioAnalyser.
//
// Why a helper: ScreenCaptureKit is Swift / Objective-C only and has implicit
// MainActor + autorelease semantics that are painful to call correctly from
// .NET Native AOT P/Invoke. Spawning a tiny native binary keeps the SCK code
// idiomatic Swift while leaving the service in C#.
//
// Permission: Screen Recording (System Settings -> Privacy & Security ->
// Screen Recording). The first run triggers the prompt; macOS attributes it
// to the parent bundle (Nexus.app) when this helper lives under
// Nexus.app/Contents/MacOS/.
//
// Wire format: little-endian IEEE float32, interleaved L/R stereo (a mono
// source repeats into both), 44100 Hz, written in raw
// frames as fast as SCK delivers them.

import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreGraphics
import AVFoundation

@available(macOS 13.0, *)
final class StreamHandler: NSObject, SCStreamOutput, SCStreamDelegate {
    private let stdout = FileHandle.standardOutput
    private let stderr = FileHandle.standardError

    // Reusable scratch buffers - audio callbacks fire ~hundreds of times/sec
    // and the project's zero-alloc-on-hot-path rule applies. Grown lazily on
    // overflow; never shrunk. Single-threaded by SCK's serial sample queue.
    private let listBytes: UnsafeMutableRawPointer
    private let listSize: Int
    private var stereoCapacity: Int = 0
    private var stereoBuffer: UnsafeMutablePointer<Float>? = nil

    override init() {
        listSize = AudioBufferList.sizeInBytes(maximumBuffers: 8)
        listBytes = UnsafeMutableRawPointer.allocate(
            byteCount: listSize,
            alignment: MemoryLayout<AudioBufferList>.alignment)
        super.init()
    }

    deinit {
        listBytes.deallocate()
        stereoBuffer?.deallocate()
    }

    func log(_ message: String) {
        if let data = (message + "\n").data(using: .utf8) {
            stderr.write(data)
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio else { return }
        guard CMSampleBufferDataIsReady(sampleBuffer) else { return }

        var blockBuffer: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: listBytes.assumingMemoryBound(to: AudioBufferList.self),
            bufferListSize: listSize,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: 0,
            blockBufferOut: &blockBuffer)
        guard status == 0 else {
            log("[audio-helper] CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer failed: \(status)")
            return
        }

        let listPtr = UnsafeMutableAudioBufferListPointer(listBytes.assumingMemoryBound(to: AudioBufferList.self))
        guard listPtr.count > 0 else { return }

        // SCK delivers deinterleaved float32 - one buffer per channel.
        let leftBuf = listPtr[0]
        let rightBuf = listPtr.count >= 2 ? listPtr[1] : leftBuf
        guard let leftPtr = leftBuf.mData?.assumingMemoryBound(to: Float.self),
              let rightPtr = rightBuf.mData?.assumingMemoryBound(to: Float.self) else { return }

        let frameCount = Int(leftBuf.mDataByteSize) / MemoryLayout<Float>.size
        guard frameCount > 0 else { return }

        if frameCount * 2 > stereoCapacity {
            stereoBuffer?.deallocate()
            stereoBuffer = UnsafeMutablePointer<Float>.allocate(capacity: frameCount * 2)
            stereoCapacity = frameCount * 2
        }
        let stereo = stereoBuffer!

        for i in 0..<frameCount {
            stereo[i * 2] = leftPtr[i]
            stereo[i * 2 + 1] = rightPtr[i]
        }

        let bytes = frameCount * 2 * MemoryLayout<Float>.size
        let data = Data(bytesNoCopy: UnsafeMutableRawPointer(stereo), count: bytes, deallocator: .none)
        stdout.write(data)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("[audio-helper] stream stopped with error: \(error.localizedDescription)")
        exit(2)
    }
}

@available(macOS 13.0, *)
@MainActor
func run() async {
    let handler = StreamHandler()

    // Trigger Screen Recording TCC registration before SCK. SCK alone
    // doesn't reliably add the helper to System Settings -> Privacy &
    // Security -> Screen Recording on Sonoma+. CGRequestScreenCaptureAccess
    // is the canonical API that registers the binary in the list AND shows
    // the prompt; SCK then inherits the grant.
    if !CGPreflightScreenCaptureAccess() {
        let granted = CGRequestScreenCaptureAccess()
        handler.log("[audio-helper] CGRequestScreenCaptureAccess returned \(granted)")
        if !granted {
            handler.log("[audio-helper] grant Screen Recording in System Settings -> Privacy & Security, then toggle Music Reactive again")
            // Exit cleanly with a distinct code so the parent process knows
            // this is a permission failure, not a crash. The next Music
            // Reactive toggle from the UI respawns us and re-checks TCC.
            exit(6)
        }
    }

    do {
        let content = try await SCShareableContent.current
        guard let display = content.displays.first else {
            handler.log("[audio-helper] no displays available")
            exit(3)
        }

        let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.sampleRate = 44100
        config.channelCount = 2
        config.excludesCurrentProcessAudio = true
        // SCStream requires non-zero video dimensions even in audio-only mode;
        // keep them at the floor so the GPU isn't doing wasted work.
        config.width = 2
        config.height = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)

        let stream = SCStream(filter: filter, configuration: config, delegate: handler)
        try stream.addStreamOutput(handler, type: .audio, sampleHandlerQueue: DispatchQueue(label: "com.hellonexus.panel.audio-helper"))
        try await stream.startCapture()
        handler.log("[audio-helper] capture started")
    } catch {
        handler.log("[audio-helper] start failed: \(error.localizedDescription)")
        exit(4)
    }

    // Stay alive forever; parent process is expected to kill us via SIGTERM
    // on shutdown. Reading stdin lets us exit cleanly when the parent's pipe
    // closes (e.g. service crash).
    let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: 4096)
    defer { buffer.deallocate() }
    while true {
        let n = read(0, buffer, 4096)
        if n <= 0 { break }
    }
    exit(0)
}

if #available(macOS 13.0, *) {
    let task = Task { await run() }
    RunLoop.main.run()
    _ = task
} else {
    FileHandle.standardError.write(Data("[audio-helper] requires macOS 13.0 or later\n".utf8))
    exit(5)
}
