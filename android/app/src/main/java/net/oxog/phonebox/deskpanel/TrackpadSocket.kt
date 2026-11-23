package net.oxog.phonebox.deskpanel

import android.os.SystemClock
import android.util.Log
import okhttp3.HttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.net.Proxy
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext

/** Used from the trackpad worker. OkHttp writes asynchronously and handles WebSocket control frames. */
internal class TrackpadSocket(private val link: AgentLink) {
    private data class Target(val hosts: List<String>, val port: Int, val pin: String? = null, val token: String? = null) {
        val label: String get() = if (pin == null) "USB" else "Wi-Fi"
    }

    private class Connection(val target: Target) {
        val handshake = CountDownLatch(1)
        var socket: WebSocket? = null
        var open = false
    }

    private val lock = Any()
    private var connection: Connection? = null
    private var epoch = 0
    private var lastFailedTarget: Target? = null
    private var retryAfter = 0L
    private val client = OkHttpClient.Builder()
        .proxy(Proxy.NO_PROXY)
        .followRedirects(false)
        .followSslRedirects(false)
        .retryOnConnectionFailure(false)
        .connectTimeout(1500, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()
    private var wifiClient: Pair<String, OkHttpClient>? = null

    fun send(json: String): Boolean {
        val target = if (link.route == AgentLink.Route.WIFI) {
            val pc = link.activePc() ?: return false
            Target(pc.hosts, pc.port, pc.id, pc.token)
        } else USB
        val socket = open(target) ?: return false
        if (socket.send(json)) return true
        disconnected(socket)
        return false
    }

    private fun open(target: Target): WebSocket? {
        val startEpoch = synchronized(lock) {
            if (connection?.target != target) disconnectLocked()
            connection?.takeIf { it.open }?.socket?.let { return it }
            if (lastFailedTarget == target && SystemClock.elapsedRealtime() < retryAfter) return null
            epoch
        }
        val http = clientFor(target)
        for (host in target.hosts) {
            val url = runCatching {
                HttpUrl.Builder().scheme(if (target.pin == null) "http" else "https")
                    .host(host).port(target.port).addPathSegments("api/trackpad").build()
            }.getOrNull() ?: continue
            val request = Request.Builder().url(url)
            target.token?.let { request.header("Authorization", "Bearer $it") }
            val candidate = Connection(target)
            synchronized(lock) {
                if (epoch != startEpoch) return null
                connection = candidate
                candidate.socket = http.newWebSocket(request.build(), listener(candidate))
            }
            val timeout = if (target.pin == null) 500L else 2000L
            candidate.handshake.await(timeout, TimeUnit.MILLISECONDS)
            synchronized(lock) {
                if (epoch != startEpoch) return null
                if (connection === candidate) {
                    if (candidate.open) return candidate.socket
                    disconnectLocked()
                }
            }
        }
        synchronized(lock) {
            if (epoch == startEpoch) {
                lastFailedTarget = target
                retryAfter = SystemClock.elapsedRealtime() + 3000
            }
        }
        return null
    }

    private fun clientFor(target: Target): OkHttpClient {
        val pin = target.pin ?: return client
        wifiClient?.takeIf { it.first == pin }?.let { return it.second }
        val trust = link.pinnedTrustManager(pin)
        val tls = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
        return client.newBuilder()
            .sslSocketFactory(tls.socketFactory, trust)
            .hostnameVerifier { _, _ -> true } // identity is the pinned certificate
            .build().also { wifiClient = pin to it }
    }

    private fun listener(candidate: Connection) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(lock) {
                if (connection === candidate) {
                    candidate.open = true
                    Log.i("PhoneBoxTrackpad", "WebSocket connected via ${candidate.target.label}")
                } else webSocket.cancel()
                candidate.handshake.countDown()
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, null)
            disconnected(webSocket)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = disconnected(webSocket)

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            response?.close()
            disconnected(webSocket)
        }
    }

    private fun disconnected(socket: WebSocket) = synchronized(lock) {
        if (connection?.socket === socket) disconnectLocked()
    }

    fun close() = synchronized(lock) {
        epoch++
        lastFailedTarget = null
        disconnectLocked()
    }

    private fun disconnectLocked() {
        val old = connection
        connection = null
        old?.handshake?.countDown()
        old?.socket?.cancel()
    }

    companion object {
        private val USB = Target(listOf("127.0.0.1"), 8765)
    }
}
