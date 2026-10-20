package com.fufucode.mobile

import com.fufucode.mobile.data.ApprovalDecision
import com.fufucode.mobile.data.LinkState
import com.fufucode.mobile.data.MobileSessionController
import com.fufucode.mobile.data.RemoteApprovalRequest
import com.fufucode.mobile.data.RemoteApprovalResolution
import com.fufucode.mobile.data.RemoteClient
import com.fufucode.mobile.data.RemoteCommandResult
import com.fufucode.mobile.data.RemotePairResult
import com.fufucode.mobile.data.RemoteServerStatus
import com.fufucode.mobile.data.RemoteSessionCatalog
import com.fufucode.mobile.data.RemoteSessionInfo
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 状态机测试。
 *
 * 覆盖点（对应任务书与用户验收关注）：
 * 1. 配对成功后写入凭据并进入在线；
 * 2. 地址规范化与非法地址拒绝；
 * 3. 发送在途去重；
 * 4. 审批单次有效：中继返回 accepted=false 时不得显示成功；
 * 5. 离线只读（canSend=false 时不允许发送）；
 * 6. 401 令牌失效：清空凭据并要求重新配对；
 * 7. 事件流中断触发退避重连；
 * 8. 重连代次淘汰旧回调（旧连接事件不得污染新状态）。
 */
@OptIn(ExperimentalCoroutinesApi::class)
class MobileSessionControllerTest {
    @Test
    fun `late pairing cannot reconnect after disconnect`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val storage = MemoryPairingStorage()
        val controller = MobileSessionController(transport, storage, backgroundScope)
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        transport.pairGate = gate
        val pairing = backgroundScope.launch { controller.pairAndConnect("host:8080", "1234") }
        controller.disconnect()
        gate.complete(Unit)
        pairing.join()
        assertNull(storage.token)
        assertEquals(LinkState.Idle, controller.state.value.link)
        assertEquals(0, transport.streamCount)
    }

    @Test
    fun `send callback requires actual acceptance and reconnect releases busy state`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("host:8080", "1234")
        var accepted = 0
        transport.sendResult = RemoteClient.Result.Ok(RemoteCommandResult(false, "rejected"))
        controller.send("retain draft") { accepted++ }
        assertEquals(0, accepted)
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        transport.sendGate = gate
        controller.send("in flight") { accepted++ }
        assertTrue(controller.state.value.sendInFlight)
        controller.connect(forceFresh = true)
        assertFalse(controller.state.value.sendInFlight)
        gate.complete(Unit)
        assertEquals(0, accepted)
        controller.release()
    }

    private fun fixture(
        transport: FakeTransport = FakeTransport(),
        storage: MemoryPairingStorage = MemoryPairingStorage(),
        scope: TestScope,
    ) = Triple(transport, storage, MobileSessionController(transport, storage, scope))

    @Test
    fun `normalizeBaseUrl accepts real forms and rejects guesses`() {
        assertEquals("http://192.168.1.5:8080", MobileSessionController.normalizeBaseUrl("192.168.1.5:8080"))
        assertEquals("http://192.168.1.5:8080", MobileSessionController.normalizeBaseUrl("http://192.168.1.5:8080/"))
        assertEquals("https://host:443", MobileSessionController.normalizeBaseUrl("https://host:443"))
        // 没有端口的输入必须拒绝，不能猜一个默认端口。
        assertNull(MobileSessionController.normalizeBaseUrl("192.168.1.5"))
        assertNull(MobileSessionController.normalizeBaseUrl(""))
        assertNull(MobileSessionController.normalizeBaseUrl("host:notaport"))
        assertNull(MobileSessionController.normalizeBaseUrl("host:99999"))
        assertNull(MobileSessionController.normalizeBaseUrl("http://host:8080/extra/path"))
        assertNull(MobileSessionController.normalizeBaseUrl("http://user:password@host:8080"))
        assertNull(MobileSessionController.normalizeBaseUrl("http://host:8080#fragment"))
        assertNull(MobileSessionController.normalizeBaseUrl("http://:8080"))
        assertEquals("http://[::1]:8080", MobileSessionController.normalizeBaseUrl("[::1]:8080"))
    }

    @Test
    fun `pairing stores credentials and reaches online`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val storage = MemoryPairingStorage()
        val controller = MobileSessionController(transport, storage, backgroundScope)

        val ok = controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        assertTrue(ok)
        assertEquals("http://192.168.1.5:8080", storage.baseUrl)
        assertEquals("token-1", storage.token)
        assertEquals(LinkState.Online, controller.state.value.link)
        assertEquals("测试会话", controller.state.value.title)
    }

    @Test
    fun `pairing failure keeps user on idle with server message`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            pairResult = RemoteClient.Result.Http(
                401,
                """{"success":false,"error":"配对码不正确，还可尝试 4 次。"}""",
                "配对码不正确，还可尝试 4 次。",
            )
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)

        val ok = controller.pairAndConnect("192.168.1.5:8080", "0000")
        advanceUntilIdle()

        assertFalse(ok)
        assertEquals(LinkState.Idle, controller.state.value.link)
        assertEquals("配对码不正确，还可尝试 4 次。", controller.state.value.notice)
    }

    @Test
    fun `invalid address is rejected before any request`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)

        val ok = controller.pairAndConnect("192.168.1.5", "1234")
        assertFalse(ok)
        assertTrue(controller.state.value.notice!!.contains("地址无效"))
    }

    @Test
    fun `send is deduplicated while in flight`() = runTest(UnconfinedTestDispatcher()) {
        // 用显式闸门固定“请求在途”窗口：第一次发送尚未返回时，第二次必须被忽略。
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        val transport = FakeTransport().apply { sendGate = gate }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        val first = controller.send("第一条")
        val second = controller.send("第二条")
        assertEquals("闸门未放行期间不应发起第二次请求", 1, transport.sendCount)
        gate.complete(Unit)
        advanceUntilIdle()

        assertTrue(first)
        assertFalse("在途期间的重复发送必须被忽略", second)
        assertEquals(1, transport.sendCount)
    }

    @Test
    fun `send is refused when offline or not writable`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(canSend = false))
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        val accepted = controller.send("不应发出")
        assertFalse(accepted)
        assertEquals(0, transport.sendCount)
        assertTrue(controller.state.value.notice!!.contains("不可发送"))
    }

    @Test
    fun `approval accepted true removes card`() = runTest(UnconfinedTestDispatcher()) {
        val request = RemoteApprovalRequest("apv-1", "c1", "edit", "{}", "修改文件", null)
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(
                FakeTransport.emptySnapshot(canStop = true, approvals = listOf(request)),
            )
            approvalResult = RemoteClient.Result.Ok(
                RemoteApprovalResolution("apv-1", "c1", ApprovalDecision.Approve, true, null),
            )
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()
        assertEquals(1, controller.state.value.approvals.size)

        controller.resolveApproval("apv-1", ApprovalDecision.Approve)
        advanceUntilIdle()

        assertTrue(controller.state.value.approvals.isEmpty())
        assertTrue(controller.state.value.notice!!.contains("已批准一次执行"))
    }

    /**
     * 关键反例：审批在桌面端已过期/已被处理时服务端返回 accepted=false，
     * 界面必须保留卡片并标注失效，而不是显示成功。
     */
    @Test
    fun `approval stale on server keeps card and marks失效`() = runTest(UnconfinedTestDispatcher()) {
        val request = RemoteApprovalRequest("apv-2", "c2", "edit", "{}", null, null)
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(approvals = listOf(request)))
            approvalResult = RemoteClient.Result.Ok(
                RemoteApprovalResolution(
                    "apv-2",
                    "",
                    ApprovalDecision.Approve,
                    false,
                    "审批不存在或已失效。",
                ),
            )
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        controller.resolveApproval("apv-2", ApprovalDecision.Approve)
        advanceUntilIdle()

        val approval = controller.state.value.approvals.single()
        assertEquals("审批不存在或已失效。", approval.resolutionFailedReason)
        assertFalse(approval.inFlight)
        // 失效的审批不计入“待处理”数量，避免界面长期提示有待办。
        assertEquals(0, controller.state.value.pendingApprovalCount)
    }

    @Test
    fun `approval submit is deduplicated while in flight`() = runTest(UnconfinedTestDispatcher()) {
        val request = RemoteApprovalRequest("apv-3", "c3", "edit", "{}", null, null)
        val gate = kotlinx.coroutines.CompletableDeferred<Unit>()
        val transport = FakeTransport().apply {
            approvalGate = gate
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(approvals = listOf(request)))
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        controller.resolveApproval("apv-3", ApprovalDecision.Approve)
        controller.resolveApproval("apv-3", ApprovalDecision.Deny)
        gate.complete(Unit)
        advanceUntilIdle()

        assertEquals("同一审批的重复决定必须被去重", 1, transport.approvalCount)
    }

    @Test
    fun `offline approval is refused`() = runTest(UnconfinedTestDispatcher()) {
        val request = RemoteApprovalRequest("apv-4", "c4", "edit", "{}", null, null)
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(approvals = listOf(request)))
            closeStreamImmediately = true
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope, reconnectDelaysMillis = listOf(10_000))
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        assertEquals(LinkState.Reconnecting, controller.state.value.link)
        controller.resolveApproval("apv-4", ApprovalDecision.Approve)
        assertEquals(0, transport.approvalCount)
        assertTrue(controller.state.value.notice!!.contains("离线状态无法审批"))
    }

    @Test
    fun `token rejection clears pairing and requires re-pair`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val storage = MemoryPairingStorage()
        val controller = MobileSessionController(transport, storage, backgroundScope)

        // 先建立一次正常配对，确认凭据已写入。
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()
        assertNotNull(storage.token)

        // 之后令牌在服务端失效：下一次请求返回 401。
        transport.snapshotResult = RemoteClient.Result.Http(401, "", "未授权")
        controller.connect()
        advanceUntilIdle()

        assertNull("令牌失效后必须清空本地令牌", storage.token)
        assertEquals(LinkState.Offline, controller.state.value.link)
        assertTrue(controller.state.value.notice!!.contains("令牌已失效"))
    }

    @Test
    fun `disconnect clears local credentials`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val storage = MemoryPairingStorage()
        val controller = MobileSessionController(transport, storage, backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        controller.disconnect()

        assertNull(storage.token)
        assertNull(storage.baseUrl)
        assertEquals(LinkState.Idle, controller.state.value.link)
    }

    @Test
    fun `stream close triggers backoff reconnect and increments attempt`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply { closeStreamImmediately = true }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        assertEquals(LinkState.Reconnecting, controller.state.value.link)
        assertTrue("重连次数必须递增", controller.state.value.reconnectAttempt >= 1)
        // 断线后必须立即进入只读，不允许继续写。
        assertFalse(controller.state.value.canSend)
        assertFalse(controller.state.value.canStop)
    }

    /**
     * 代次淘汰反例：旧连接的增量事件在重连之后到达，必须被丢弃，
     * 不能覆盖新连接读到的会话标题。
     */
    @Test
    fun `stale events from previous generation are discarded`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()
        assertEquals("测试会话", controller.state.value.title)
        val staleStream = transport.currentStream

        // 触发一次重连：代次递增，并建立一条全新的连接。
        transport.snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(title = "新会话标题"))
        controller.connect()
        advanceUntilIdle()
        assertEquals("新会话标题", controller.state.value.title)
        assertTrue("重连必须建立新连接", transport.streamCount >= 2)

        // 向**旧连接**投递属于上一代的快照帧；其内容不得改写当前标题。
        staleStream.trySend(
            RemoteClient.EventStreamMessage.Payload(
                """{"type":"snapshot","session":{"sessionId":"old","title":"过期标题","activity":"Idle","connection":"Connected","model":null,"canSend":false,"canStop":false,"queuedCount":0,"entries":[],"pendingApprovals":[]}}""",
            ),
        )
        advanceUntilIdle()
        assertEquals("新会话标题", controller.state.value.title)
    }

    @Test
    fun `entry updates merge by id and append new ones`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        fun entryJson(id: String, text: String) =
            """{"type":"entry","entry":{"id":"$id","role":"Assistant","isStreaming":false,"model":null,"blocks":[{"kind":"text","text":"$text"}]}}"""

        transport.currentStream.trySend(RemoteClient.EventStreamMessage.Payload(entryJson("m1", "第一版")))
        advanceUntilIdle()
        transport.currentStream.trySend(RemoteClient.EventStreamMessage.Payload(entryJson("m1", "覆盖后")))
        advanceUntilIdle()
        transport.currentStream.trySend(RemoteClient.EventStreamMessage.Payload(entryJson("m2", "第二条")))
        advanceUntilIdle()

        val timeline = controller.state.value.timeline
        assertEquals(2, timeline.size)
        assertEquals("m1", timeline[0].entry.id)
        val block = timeline[0].entry.blocks.single()
        assertEquals("覆盖后", (block as com.fufucode.mobile.data.RemoteBlock.Text).text)
        assertEquals("m2", timeline[1].entry.id)
    }

    @Test
    fun `approval added and resolved updates the list`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        transport.currentStream.trySend(
            RemoteClient.EventStreamMessage.Payload(
                """{"type":"approvalAdded","approval":{"approvalId":"a9","toolCallId":"c9","toolName":"bash","argsJson":"{}","summary":null,"requestedAtUtc":null}}""",
            ),
        )
        advanceUntilIdle()
        assertEquals(1, controller.state.value.approvals.size)

        // 同一审批重复推送不得产生重复卡片。
        transport.currentStream.trySend(
            RemoteClient.EventStreamMessage.Payload(
                """{"type":"approvalAdded","approval":{"approvalId":"a9","toolCallId":"c9","toolName":"bash","argsJson":"{}","summary":null,"requestedAtUtc":null}}""",
            ),
        )
        advanceUntilIdle()
        assertEquals(1, controller.state.value.approvals.size)

        transport.currentStream.trySend(
            RemoteClient.EventStreamMessage.Payload(
                """{"type":"approvalResolved","resolution":{"approvalId":"a9","toolCallId":"c9","decision":"Deny","accepted":true,"message":null}}""",
            ),
        )
        advanceUntilIdle()
        assertTrue(controller.state.value.approvals.isEmpty())
    }

    @Test
    fun `stop is refused when server reports no cancellable work`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(canStop = false))
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        controller.stop()
        assertEquals(0, transport.stopCount)
        assertTrue(controller.state.value.notice!!.contains("没有可停止"))
    }

    @Test
    fun `stop reaches server when cancellable`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            snapshotResult = RemoteClient.Result.Ok(FakeTransport.emptySnapshot(canStop = true))
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        controller.stop()
        advanceUntilIdle()
        assertEquals(1, transport.stopCount)
        assertEquals("已发送紧急停止", controller.state.value.notice)
    }

    @Test
    fun `server status is recorded for device page`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            statusResult = RemoteClient.Result.Ok(
                RemoteServerStatus(true, "192.168.1.5", 8080, 1, "http://192.168.1.5:8080/", "1234", "12:00:00", null),
            )
        }
        val storage = MemoryPairingStorage()
        val controller = MobileSessionController(transport, storage, backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        assertEquals(8080, controller.state.value.server?.port)
        assertEquals(8080, storage.lastPort)
    }

    @Test
    fun `send server rejection surfaces message and is not treated as success`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            sendResult = RemoteClient.Result.Ok(RemoteCommandResult(false, "当前会话不可发送（可能在停止中或未连接）。"))
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()

        assertTrue(controller.send("hello"))
        advanceUntilIdle()
        assertEquals("当前会话不可发送（可能在停止中或未连接）。", controller.state.value.notice)
    }

    @Test
    fun `connectIfPaired does nothing without credentials`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport()
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.connectIfPaired()
        advanceUntilIdle()
        // 无凭据时不应发起任何连接请求，状态保持 Idle。
        assertEquals(0, transport.snapshotCount)
        assertEquals(LinkState.Idle, controller.state.value.link)
    }

    @Test
    fun `session catalog is loaded and accepted switch refreshes snapshot`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            sessionsResult = RemoteClient.Result.Ok(
                RemoteSessionCatalog(
                    "one",
                    listOf(
                        RemoteSessionInfo("one", "会话一", "项目甲", "FUMO", true, false, false, false, null),
                        RemoteSessionInfo("two", "会话二", "项目甲", "OMP", false, true, true, true, null),
                    ),
                ),
            )
            snapshotResult = RemoteClient.Result.Ok(
                FakeTransport.emptySnapshot().copy(sessionId = "one", title = "会话一"),
            )
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()
        assertEquals(2, controller.state.value.sessions.size)
        assertEquals("会话二", controller.state.value.sessions[1].title)

        transport.switchResult = RemoteClient.Result.Ok(RemoteCommandResult(true, "已切换"))
        transport.snapshotResult = RemoteClient.Result.Ok(
            FakeTransport.emptySnapshot().copy(sessionId = "two", title = "会话二"),
        )
        controller.switchSession("two")
        advanceUntilIdle()
        assertEquals(1, transport.switchCount)
        assertEquals("two", controller.state.value.sessionId)
        assertEquals("会话二", controller.state.value.title)
    }

    @Test
    fun `session switch is not claimed when desktop rejects it`() = runTest(UnconfinedTestDispatcher()) {
        val transport = FakeTransport().apply {
            sessionsResult = RemoteClient.Result.Ok(
                RemoteSessionCatalog(
                    "one",
                    listOf(
                        RemoteSessionInfo("one", "会话一", "项目甲", "FUMO", true, false, false, false, null),
                        RemoteSessionInfo("two", "会话二", "项目甲", "OMP", false, false, false, true, null),
                    ),
                ),
            )
            switchResult = RemoteClient.Result.Ok(RemoteCommandResult(false, "电脑端忙碌"))
            snapshotResult = RemoteClient.Result.Ok(
                FakeTransport.emptySnapshot().copy(sessionId = "one", title = "会话一"),
            )
        }
        val controller = MobileSessionController(transport, MemoryPairingStorage(), backgroundScope)
        controller.pairAndConnect("192.168.1.5:8080", "1234")
        advanceUntilIdle()
        controller.switchSession("two")
        advanceUntilIdle()
        assertEquals("one", controller.state.value.sessionId)
        assertEquals("电脑端忙碌", controller.state.value.notice)
    }
}
