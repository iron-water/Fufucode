package com.fufucode.mobile.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.atomic.AtomicInteger

/**
 * 局域网自动发现。
 *
 * **范围边界（如实说明）**：桌面端用 `StartAsync(0)` 时端口由操作系统临时分配，
 * 落在 Windows 默认动态端口区间（49152-65535）。254 主机 × 16384 端口在该区间内
 * 全量扫描在实际时间内不可行，也没有必要——桌面端面板会直接显示 IP:端口 与二维码。
 *
 * 因此本实现采用**可解释且有界**的策略：
 * 1. 端口候选 = 上次连接成功的端口（若有）+ [defaultPorts] 小集合；
 * 2. 枚举本机非回环 IPv4 的 /24 子网逐主机探测；
 * 3. 先 TCP 预筛，再按页面标记确认确为 Fufucode 远控服务；
 * 4. 界面展示本次**实际扫描的子网与端口集合**，不暗示“已扫全网”。
 *
 * 首次连接请用桌面面板的 IP:端口 + 4 位配对码，或扫码；之后自动发现即可命中。
 */
class LanDiscovery(
    private val client: RemoteClient,
    private val tcpTimeoutMillis: Int = 200,
) {
    companion object {
        /**
         * 默认候选端口：桌面端可固定使用的常见端口。
         * 有意保持小集合，避免不可完成的暴力扫描。
         */
        val defaultPorts: List<Int> = listOf(8080, 8000, 5000, 3000, 7788, 12580)

        private const val HOSTS_PER_SUBNET = 254
        private const val MAX_CONCURRENT_PROBES = 96
    }

    sealed interface ScanEvent {
        /** 扫描开始：本次实际枚举到的子网前缀与实际端口候选。 */
        data class Started(val prefixes: List<String>, val ports: List<Int>) : ScanEvent
        data class Progress(val scannedHosts: Int, val totalHosts: Int) : ScanEvent
        data class Found(val baseUrl: String) : ScanEvent
        data class Finished(val scannedHosts: Int, val reason: String?) : ScanEvent
    }

    /** 枚举本机可路由的 IPv4 子网前缀（形如 `192.168.1.`）。 */
    fun localSubnetPrefixes(): List<String> = try {
        NetworkInterface.getNetworkInterfaces().asSequence()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { nic -> nic.interfaceAddresses.asSequence() }
            .mapNotNull { address ->
                val host = address.address
                if (host is Inet4Address && !host.isLoopbackAddress && !host.isLinkLocalAddress) {
                    val bytes = host.address
                    "${bytes[0].toInt() and 0xFF}.${bytes[1].toInt() and 0xFF}.${bytes[2].toInt() and 0xFF}."
                } else {
                    null
                }
            }
            .distinct()
            .toList()
    } catch (_: Exception) {
        emptyList()
    }

    /**
     * 扫描并逐个产出**已确认**的桌面端地址；确认一个立即 emit，界面边扫边显示。
     *
     * @param rememberedPort 上次连接成功的端口，优先探测。
     * @param manualHost 用户手动指定的主机（例如桌面面板上显示的 IP），优先探测。
     */
    fun scan(rememberedPort: Int? = null, manualHost: String? = null): Flow<ScanEvent> = channelFlow {
        val prefixes = localSubnetPrefixes()
        val ports = buildList {
            rememberedPort?.takeIf { it in 1..65535 }?.let { add(it) }
            addAll(defaultPorts)
        }.distinct()

        send(ScanEvent.Started(prefixes, ports))

        val candidates = LinkedHashSet<String>()
        manualHost?.trim()?.takeIf { it.isNotEmpty() }?.let { candidates.add(it) }
        for (prefix in prefixes) {
            for (last in 1..HOSTS_PER_SUBNET) candidates.add("$prefix$last")
        }

        if (candidates.isEmpty()) {
            send(ScanEvent.Finished(0, "未发现可用的局域网 IPv4 地址；请确认已连接 Wi-Fi"))
            return@channelFlow
        }

        val total = candidates.size
        val scanned = AtomicInteger(0)
        val permits = Semaphore(MAX_CONCURRENT_PROBES)

        // 进度上报与探测并行：每完成约 5% 上报一次，避免长时间静默。
        val reporter = launch {
            var lastPercent = -1
            while (true) {
                delay(300)
                val done = scanned.get()
                val percent = if (total == 0) 100 else (done * 100 / total)
                if (percent != lastPercent && (percent - lastPercent >= 5 || done >= total)) {
                    lastPercent = percent
                    send(ScanEvent.Progress(done, total))
                }

                if (done >= total) break
            }
        }

        coroutineScope {
            candidates.map { host ->
                async(Dispatchers.IO) {
                    permits.withPermit {
                        try {
                            for (port in ports) {
                                if (!isTcpOpen(host, port)) continue
                                when (val probe = client.probeDesktop(host, port, timeoutMillis = tcpTimeoutMillis * 3L)) {
                                    is RemoteClient.ProbeResult.Found -> send(ScanEvent.Found(probe.baseUrl))
                                    else -> Unit
                                }
                            }
                        } finally {
                            scanned.incrementAndGet()
                        }
                    }
                }
            }.awaitAll()
        }

        reporter.cancel()
        send(ScanEvent.Progress(scanned.get(), total))
        send(ScanEvent.Finished(scanned.get(), null))
    }

    private suspend fun isTcpOpen(host: String, port: Int): Boolean = withContext(Dispatchers.IO) {
        try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), tcpTimeoutMillis)
                true
            }
        } catch (_: Exception) {
            false
        }
    }
}
