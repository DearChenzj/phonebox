package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import java.security.SecureRandom
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString

/** The microphone is armed in the foreground and starts only when the Mac opens its virtual audio routes. */
internal class DesktopAudio(private val link: AgentLink, private val status: (String) -> Unit) {
    private val connection = AtomicReference<Connection?>()

    fun start() {
        if (connection.get() != null) return
        val pc = link.activePc()
        if (pc == null || pc.hosts.isEmpty()) {
            status("请先通过 Wi-Fi 配对 Mac。")
            return
        }
        val next = Connection(pc)
        if (!connection.compareAndSet(null, next)) {
            next.close()
            return
        }
        next.connect(0)
    }

    fun stop() {
        connection.getAndSet(null)?.close()
        status("off")
    }

    private inner class Connection(private val pc: AgentLink.Pc) {
        private val manager = link.pinnedTrustManager(pc.id)
        private val tls = SSLContext.getInstance("TLS").apply { init(null, arrayOf(manager), SecureRandom()) }
        private val client = OkHttpClient.Builder()
            .sslSocketFactory(tls.socketFactory, manager)
            .hostnameVerifier { _, _ -> true } // the trust manager pins the paired Mac's certificate
            .connectTimeout(3, TimeUnit.SECONDS)
            .pingInterval(15, TimeUnit.SECONDS)
            .build()
        @Volatile private var socket: WebSocket? = null
        @Volatile private var audio: Pipe? = null

        @Synchronized
        fun connect(index: Int) {
            if (connection.get() !== this) return
            val request = Request.Builder()
                .url("wss://${pc.hosts[index]}:${pc.port}/api/desktop-audio/socket?startCodex=1")
                .header("Authorization", "Bearer ${pc.token}")
                .build()
            socket = client.newWebSocket(request, object : WebSocketListener() {
                private var upgraded = false

                override fun onOpen(webSocket: WebSocket, response: Response) {
                    upgraded = true
                    if (connection.get() !== this@Connection) webSocket.cancel()
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    when (text) {
                        "waiting" -> waitForDesktop()
                        "active" -> startAudio(webSocket)
                        "start-error:closed" -> status("请先在 Mac 打开 Codex 会话，再重试。也可以从 Mac 手动开启语音。")
                        "start-error:permission" -> status("Mac 未能发送语音快捷键。请检查 PhoneBoxAgent 的辅助功能权限，或从 Mac 手动开启语音。")
                    }
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    if (connection.get() === this@Connection && bytes.size == PACKET_BYTES)
                        audio?.enqueue(bytes.toByteArray())
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(code, null)
                    disconnected("Mac 已断开音频，请检查虚拟音频设备。")
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (connection.get() !== this@Connection) return
                    if (!upgraded && response == null && index + 1 < pc.hosts.size) {
                        connect(index + 1)
                        return
                    }
                    disconnected(if (response?.code == 409) "另一台手机已连接到 Mac 音频。"
                        else "无法连接 Mac 音频，请检查 Wi-Fi 和桌面代理。")
                }
            })
        }

        @Synchronized
        private fun waitForDesktop() {
            if (connection.get() !== this) return
            audio?.close()
            audio = null
            status("waiting")
        }

        @SuppressLint("MissingPermission")
        @Synchronized
        private fun startAudio(webSocket: WebSocket) {
            if (connection.get() !== this || audio != null) return
            try {
                val next = Pipe(webSocket) { disconnected("手机麦克风或扬声器无法使用。") }
                audio = next
                next.start()
                status("active")
            } catch (_: RuntimeException) {
                disconnected("手机麦克风或扬声器无法使用。")
            }
        }

        private fun disconnected(reason: String) {
            if (connection.compareAndSet(this, null)) {
                close()
                status(reason)
            }
        }

        @Synchronized
        fun close() {
            audio?.close()
            audio = null
            socket?.cancel()
            socket = null
            client.connectionPool.evictAll()
            client.dispatcher.executorService.shutdown()
        }
    }

    @SuppressLint("MissingPermission")
    private class Pipe(private val socket: WebSocket, private val error: () -> Unit) {
        @Volatile private var running = false
        private val speakerQueue = ArrayBlockingQueue<ByteArray>(6)
        private val microphone: AudioRecord
        private val speaker: AudioTrack

        init {
            val inputMin = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            val outputMin = AudioTrack.getMinBufferSize(RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
            check(inputMin > 0 && outputMin > 0)
            microphone = AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT, maxOf(inputMin, PACKET_BYTES * 3))
            try {
                check(microphone.state == AudioRecord.STATE_INITIALIZED)
                val attributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()
                val format = AudioFormat.Builder().setSampleRate(RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()
                speaker = AudioTrack(attributes, format, maxOf(outputMin, PACKET_BYTES * 3),
                    AudioTrack.MODE_STREAM, AudioManager.AUDIO_SESSION_ID_GENERATE)
            } catch (error: RuntimeException) {
                microphone.release()
                throw error
            }
        }

        fun start() {
            try {
                check(speaker.state == AudioTrack.STATE_INITIALIZED)
                microphone.startRecording()
                check(microphone.recordingState == AudioRecord.RECORDSTATE_RECORDING)
                speaker.play()
                running = true
            } catch (problem: RuntimeException) {
                microphone.release()
                speaker.release()
                throw problem
            }
            Thread({
                try {
                    val bytes = ByteArray(PACKET_BYTES)
                    var used = 0
                    while (running) {
                        val count = microphone.read(bytes, used, bytes.size - used)
                        if (count <= 0) break
                        used += count
                        if (used == bytes.size) {
                            if (socket.queueSize() < PACKET_BYTES * 8L && !socket.send(bytes.toByteString())) break
                            used = 0
                        }
                    }
                } catch (_: RuntimeException) {
                    // Audio can disappear when Android revokes the route.
                } finally {
                    if (running) error()
                    microphone.release()
                }
            }, "desktop-audio-mic").apply { isDaemon = true; start() }
            Thread({
                try {
                    while (running) {
                        val packet = speakerQueue.poll(80, TimeUnit.MILLISECONDS) ?: continue
                        var written = 0
                        while (running && written < packet.size) {
                            val count = speaker.write(packet, written, packet.size - written)
                            check(count > 0)
                            written += count
                        }
                    }
                } catch (_: RuntimeException) {
                    // Notify the connection and release the audio device below.
                } finally {
                    if (running) error()
                    speaker.release()
                }
            }, "desktop-audio-speaker").apply { isDaemon = true; start() }
        }

        fun enqueue(packet: ByteArray) {
            if (!speakerQueue.offer(packet)) {
                speakerQueue.poll()
                speakerQueue.offer(packet)
            }
        }

        fun close() {
            if (!running) return
            running = false
            runCatching { microphone.stop() }
            runCatching { speaker.pause() }
            runCatching { speaker.flush() }
            speakerQueue.clear()
        }
    }

    private companion object {
        const val RATE = 48_000
        const val PACKET_BYTES = 1_920
    }
}
