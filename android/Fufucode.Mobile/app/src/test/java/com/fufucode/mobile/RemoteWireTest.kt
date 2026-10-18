package com.fufucode.mobile

import com.fufucode.mobile.data.ApprovalDecision
import com.fufucode.mobile.data.EntryRole
import com.fufucode.mobile.data.RemoteBlock
import com.fufucode.mobile.data.RemoteUpdate
import com.fufucode.mobile.data.RemoteWire
import com.fufucode.mobile.data.ToolStatus
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 线协议解析测试。
 *
 * 夹具直接取自桌面端 `RemoteWire.Options`（camelCase + 枚举按名称上线）的实际形状，
 * 每一条都对应 `RemoteControlModels.cs` / `RemoteControlUpdates.cs` 里的真实字段。
 */
class RemoteWireTest {

    @Test
    fun `pairResult parses success and lockout`() {
        val ok = RemoteWire.parsePairResult("""{"success":true,"token":"tk","error":null,"lockedOut":false}""")
        assertNotNull(ok)
        assertTrue(ok!!.success)
        assertEquals("tk", ok.token)

        val locked = RemoteWire.parsePairResult(
            """{"success":false,"token":null,"error":"尝试次数过多，请稍后再试。","lockedOut":true}""",
        )
        assertNotNull(locked)
        assertTrue(locked!!.lockedOut)
        assertEquals("尝试次数过多，请稍后再试。", locked.error)
    }

    @Test
    fun `snapshot parses polymorphic blocks by kind`() {
        val body = """
        {
          "sessionId":"abc","title":"重构会话列表状态机",
          "activity":"Running","connection":"Connected","model":"gpt-6.1-sol",
          "canSend":true,"canStop":true,"queuedCount":2,
          "entries":[
            {"id":"m1","role":"User","isStreaming":false,"model":null,
             "blocks":[{"kind":"text","text":"先把列表改成按项目分组"}]},
            {"id":"m2","role":"Assistant","isStreaming":true,"model":"gpt-6.1-sol",
             "blocks":[
               {"kind":"thinking","text":"先确认现有实现。"},
               {"kind":"text","text":"正在读取"},
               {"kind":"tool","tool":{"toolCallId":"c1","toolName":"read","argsJson":"{\"path\":\"a.cs\"}","status":"AwaitingApproval","statusText":"等待审批","output":null,"isErrorOutput":false}}
             ]},
            {"id":"e1","role":"Assistant","isStreaming":false,"model":null,
             "blocks":[{"kind":"error","message":"连接中断"}]}
          ],
          "pendingApprovals":[
            {"approvalId":"apv-1","toolCallId":"c1","toolName":"read","argsJson":"{}","summary":"读取文件","requestedAtUtc":"2026-10-15T00:00:00+00:00"}
          ]
        }
        """.trimIndent()

        val snapshot = RemoteWire.parseSnapshot(body)
        assertNotNull(snapshot)
        val s = snapshot!!
        assertEquals("重构会话列表状态机", s.title)
        assertEquals(2, s.queuedCount)
        assertTrue(s.canSend)
        assertTrue(s.canStop)
        assertEquals(3, s.entries.size)

        assertEquals(EntryRole.User, s.entries[0].role)
        assertEquals(RemoteBlock.Text("先把列表改成按项目分组"), s.entries[0].blocks[0])

        val assistant = s.entries[1]
        assertTrue(assistant.isStreaming)
        assertEquals(RemoteBlock.Thinking("先确认现有实现。"), assistant.blocks[0])
        assertEquals(RemoteBlock.Text("正在读取"), assistant.blocks[1])
        val tool = assistant.blocks[2] as RemoteBlock.Tool
        assertEquals("read", tool.call.toolName)
        assertEquals(ToolStatus.AwaitingApproval, tool.call.status)

        assertTrue(s.entries[2].blocks[0] is RemoteBlock.Error)
        assertEquals(1, s.pendingApprovals.size)
        assertEquals("apv-1", s.pendingApprovals[0].approvalId)
    }

    @Test
    fun `update parses every wire type`() {
        val entry = RemoteWire.parseUpdate(
            """{"type":"entry","entry":{"id":"m1","role":"Assistant","isStreaming":false,"model":null,"blocks":[{"kind":"text","text":"ok"}]}}""",
        )
        assertTrue(entry is RemoteUpdate.Entry)

        val approvalAdded = RemoteWire.parseUpdate(
            """{"type":"approvalAdded","approval":{"approvalId":"a1","toolCallId":"t1","toolName":"edit","argsJson":"{}","summary":null,"requestedAtUtc":null}}""",
        )
        assertTrue(approvalAdded is RemoteUpdate.ApprovalAdded)

        val resolved = RemoteWire.parseUpdate(
            """{"type":"approvalResolved","resolution":{"approvalId":"a1","toolCallId":"t1","decision":"Approve","accepted":true,"message":null}}""",
        )
        assertTrue(resolved is RemoteUpdate.ApprovalResolved)
        assertEquals(
            ApprovalDecision.Approve,
            (resolved as RemoteUpdate.ApprovalResolved).resolution.decision,
        )

        val status = RemoteWire.parseUpdate(
            """{"type":"status","activity":"Idle","connection":"Connected","canSend":true,"canStop":false,"queuedCount":0}""",
        )
        assertEquals("Idle", (status as RemoteUpdate.Status).activity)

        val notice = RemoteWire.parseUpdate("""{"type":"notice","message":"本轮任务已完成。"}""")
        assertEquals("本轮任务已完成。", (notice as RemoteUpdate.Notice).message)

        val snapshotUpdate = RemoteWire.parseUpdate(
            """{"type":"snapshot","session":{"sessionId":"s","title":"t","activity":"Idle","connection":"Connected","model":null,"canSend":false,"canStop":false,"queuedCount":0,"entries":[],"pendingApprovals":[]}}""",
        )
        assertTrue(snapshotUpdate is RemoteUpdate.Snapshot)
    }

    @Test
    fun `unknown wire type is recorded not thrown`() {
        RemoteWire.clearDiagnostics()
        val unknown = RemoteWire.parseUpdate("""{"type":"brandNewFrameFromFuture","payload":{}}""")
        assertNull(unknown)
        val diagnostics = RemoteWire.diagnostics_
        assertTrue("未知帧类型必须被记录", diagnostics.any { it.detail.contains("brandNewFrameFromFuture") })
    }

    @Test
    fun `unknown block kind is recorded and dropped without failing the whole entry`() {
        RemoteWire.clearDiagnostics()
        val body = """
        {"id":"m1","role":"Assistant","isStreaming":false,"model":null,
         "blocks":[{"kind":"text","text":"保留"},{"kind":"future-block","x":1}]}
        """.trimIndent()
        val update = RemoteWire.parseUpdate("""{"type":"entry","entry":$body}""")
        val entry = (update as RemoteUpdate.Entry).entry
        assertEquals(1, entry.blocks.size)
        assertEquals(RemoteBlock.Text("保留"), entry.blocks[0])
        assertTrue(RemoteWire.diagnostics_.any { it.detail.contains("future-block") })
    }

    @Test
    fun `malformed json does not throw and yields null`() {
        assertNull(RemoteWire.parseSnapshot("{ not json"))
        assertNull(RemoteWire.parseUpdate(""))
        assertNull(RemoteWire.parseCommandResult("<html>401</html>"))
    }

    @Test
    fun `server status maps running state and port`() {
        val status = RemoteWire.parseStatus(
            """{"state":"Running","address":"192.168.1.5","port":8080,"connectionCount":1,"pairingUrl":"http://192.168.1.5:8080/","pin":"1234","tokenLifetime":"12:00:00","startedAtUtc":"2026-10-15T01:02:03+00:00"}""",
        )
        assertNotNull(status)
        assertTrue(status!!.running)
        assertEquals(8080, status.port)
        assertEquals("192.168.1.5", status.address)
        assertEquals("12:00:00", status.tokenLifetimeText)
    }

    @Test
    fun `approval resolution with accepted false is preserved as失效`() {
        val resolution = RemoteWire.parseApprovalResolution(
            """{"approvalId":"apv-9","toolCallId":"","decision":"Deny","accepted":false,"message":"审批不存在或已失效。"}""",
        )
        assertNotNull(resolution)
        assertFalse(resolution!!.accepted)
        assertEquals("审批不存在或已失效。", resolution.message)
        assertEquals(ApprovalDecision.Deny, resolution.decision)
    }
}
