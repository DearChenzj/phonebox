import AudioToolbox
import CoreAudio
import Darwin
import Foundation

// PhoneBox exchanges 20 ms frames of little-endian, 48 kHz mono PCM.
private let frames = 960
private let maxSamples = frames * 6
private let host = AudioObjectID(kAudioObjectSystemObject)

private func property(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope =
    kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
}

private func ids(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                 _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> [AudioObjectID] {
    var key = property(selector, scope)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(object, &key, 0, nil, &size) == noErr, size > 0 else { return [] }
    var values = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.stride)
    let result = values.withUnsafeMutableBytes { AudioObjectGetPropertyData(object, &key, 0, nil, &size, $0.baseAddress!) }
    return result == noErr ? Array(values.prefix(Int(size) / MemoryLayout<AudioObjectID>.stride)) : []
}

private func integer(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> UInt32 {
    var key = property(selector)
    var value: UInt32 = 0
    var size = UInt32(MemoryLayout<UInt32>.size)
    return AudioObjectGetPropertyData(object, &key, 0, nil, &size, &value) == noErr ? value : 0
}

private func string(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
    var key = property(selector)
    var result: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout.size(ofValue: result))
    return AudioObjectGetPropertyData(object, &key, 0, nil, &size, &result) == noErr
        ? result?.takeRetainedValue() as String? : nil
}

@available(macOS 14.2, *)
private func desktopWantsAudio(microphone: AudioObjectID) -> Bool {
    for process in ids(host, kAudioHardwarePropertyProcessObjectList) {
        // The bridge uses the opposite directions on the devices. Its own I/O should never keep a call active.
        if integer(process, kAudioProcessPropertyPID) == UInt32(getpid()) { continue }
        if integer(process, kAudioProcessPropertyIsRunningInput) != 0 &&
            ids(process, kAudioProcessPropertyDevices, kAudioObjectPropertyScopeInput).contains(microphone) { return true }
    }
    return false
}

private struct AudioFailure: Error {
    let operation: String
    let status: OSStatus
}

private func check(_ status: OSStatus, _ operation: String) throws {
    if status != noErr { throw AudioFailure(operation: operation, status: status) }
}

private final class Frames {
    private let lock = NSLock()
    private var microphone: [Int16] = []
    private var speaker: [Int16] = []
    private var active = false

    func setActive(_ value: Bool) {
        lock.lock()
        defer { lock.unlock() }
        active = value
        if !active {
            microphone.removeAll(keepingCapacity: true)
            speaker.removeAll(keepingCapacity: true)
        }
    }

    func receive(_ data: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard active else { return }
        data.withUnsafeBytes { bytes in
            for sample in bytes.bindMemory(to: Int16.self) { microphone.append(Int16(littleEndian: sample)) }
        }
        if microphone.count > maxSamples { microphone.removeFirst(microphone.count - maxSamples) }
    }

    func play(_ buffer: AudioQueueBufferRef) {
        lock.lock()
        defer { lock.unlock() }
        let stereo = buffer.pointee.mAudioData.assumingMemoryBound(to: Int16.self)
        let available = active ? min(frames, microphone.count) : 0
        for index in 0..<frames {
            let sample = index < available ? microphone[index] : 0
            stereo[index * 2] = sample
            stereo[index * 2 + 1] = sample
        }
        microphone.removeFirst(available)
        buffer.pointee.mAudioDataByteSize = UInt32(frames * 4)
    }

    func capture(_ buffer: AudioQueueBufferRef) {
        lock.lock()
        defer { lock.unlock() }
        guard active else { return }
        let stereo = buffer.pointee.mAudioData.assumingMemoryBound(to: Int16.self)
        for index in 0..<(Int(buffer.pointee.mAudioDataByteSize) / 4) {
            let left = Int32(stereo[index * 2])
            let right = Int32(stereo[index * 2 + 1])
            speaker.append(Int16((left + right) / 2))
        }
        if speaker.count > maxSamples { speaker.removeFirst(speaker.count - maxSamples) }
    }

    func nextPacket() -> Data? {
        lock.lock()
        defer { lock.unlock() }
        guard speaker.count >= frames else { return nil }
        var data = Data([2])
        for value in speaker.prefix(frames) {
            var sample = value.littleEndian
            withUnsafeBytes(of: &sample) { data.append(contentsOf: $0) }
        }
        speaker.removeFirst(frames)
        return data
    }
}

private final class AudioPair {
    let buffers: Frames
    var capture: AudioQueueRef?
    var playback: AudioQueueRef?

    init(microphone: String, speaker: String, buffers: Frames) throws {
        self.buffers = buffers
        var format = AudioStreamBasicDescription(mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: 2,
            mBitsPerChannel: 16, mReserved: 0)
        let context = Unmanaged.passUnretained(buffers).toOpaque()
        do {
            try check(AudioQueueNewInput(&format, { context, queue, buffer, _, _, _ in
                guard let context else { return }
                Unmanaged<Frames>.fromOpaque(context).takeUnretainedValue().capture(buffer)
                AudioQueueEnqueueBuffer(queue, buffer, 0, nil)
            }, context, nil, nil, 0, &capture), "Open speaker loopback")
            try check(AudioQueueNewOutput(&format, { context, queue, buffer in
                guard let context else { return }
                Unmanaged<Frames>.fromOpaque(context).takeUnretainedValue().play(buffer)
                AudioQueueEnqueueBuffer(queue, buffer, 0, nil)
            }, context, nil, nil, 0, &playback), "Open microphone loopback")
            try configure(capture!, device: speaker, isOutput: false)
            try configure(playback!, device: microphone, isOutput: true)
            try check(AudioQueueStart(capture!, nil), "Start speaker loopback")
            try check(AudioQueueStart(playback!, nil), "Start microphone loopback")
        } catch {
            stop()
            throw error
        }
    }

    private func configure(_ queue: AudioQueueRef, device: String, isOutput: Bool) throws {
        let uid = device as CFString
        try withUnsafePointer(to: uid) {
            try check(AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, $0,
                                            UInt32(MemoryLayout<CFString>.size)), "Select virtual audio device")
        }
        for _ in 0..<3 {
            var raw: AudioQueueBufferRef?
            try check(AudioQueueAllocateBuffer(queue, UInt32(frames * 4), &raw), "Allocate audio buffer")
            guard let buffer = raw else { throw AudioFailure(operation: "Allocate audio buffer", status: -1) }
            if isOutput { buffers.play(buffer) }
            try check(AudioQueueEnqueueBuffer(queue, buffer, 0, nil), "Queue audio buffer")
        }
    }

    func stop() {
        if let capture { AudioQueueDispose(capture, true) }
        if let playback { AudioQueueDispose(playback, true) }
        capture = nil
        playback = nil
    }

    deinit { stop() }
}

private func run() throws {
    guard #available(macOS 14.2, *) else {
        throw AudioFailure(operation: "macOS 14.2 or later is required", status: -1)
    }
    let args = Array(CommandLine.arguments.dropFirst())
    guard args.count == 3, args[0] == "--run" || args[0] == "--check", args[1] != args[2] else {
        throw AudioFailure(operation: "Provide --run or --check and two different virtual-device names", status: -1)
    }
    let devices = ids(host, kAudioHardwarePropertyDevices)
    guard let microphone = devices.first(where: { string($0, kAudioObjectPropertyName) == args[1] }),
          let speaker = devices.first(where: { string($0, kAudioObjectPropertyName) == args[2] }),
          let micUID = string(microphone, kAudioDevicePropertyDeviceUID),
          let speakerUID = string(speaker, kAudioDevicePropertyDeviceUID),
          integer(microphone, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeVirtual,
          integer(speaker, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeVirtual else {
        throw AudioFailure(operation: "The two virtual audio devices are unavailable", status: -1)
    }
    if args[0] == "--check" { return }
    let buffers = Frames()
    DispatchQueue.global(qos: .userInteractive).async {
        while true {
            do {
                // The agent writes fixed-size packets. Coalesce partial pipe reads without splitting Int16 samples.
                var packet = Data()
                while packet.count < frames * 2 {
                    guard let chunk = try FileHandle.standardInput.read(upToCount: frames * 2 - packet.count), !chunk.isEmpty
                    else { exit(0) }
                    packet.append(chunk)
                }
                buffers.receive(packet)
            } catch { exit(1) }
        }
    }

    let output = FileHandle.standardOutput
    var pair: AudioPair?
    var announcedState = false
    var lastCheck = Date.distantPast
    while true {
        if Date().timeIntervalSince(lastCheck) >= 0.25 {
            lastCheck = Date()
            let active = desktopWantsAudio(microphone: microphone)
            if active, pair == nil {
                buffers.setActive(true)
                pair = try AudioPair(microphone: micUID, speaker: speakerUID, buffers: buffers)
                try output.write(contentsOf: Data([1]))
            } else if !active, let oldPair = pair {
                buffers.setActive(false)
                oldPair.stop()
                pair = nil
                try output.write(contentsOf: Data([0]))
            } else if !announcedState {
                try output.write(contentsOf: Data([0]))
            }
            announcedState = true
        }
        while let packet = buffers.nextPacket() { try output.write(contentsOf: packet) }
        Thread.sleep(forTimeInterval: 0.01)
    }
}

do {
    try run()
} catch {
    FileHandle.standardError.write(Data(("PhoneBox audio: \(error)\n").utf8))
    exit(1)
}
