package com.fufucode.mobile

import com.fufucode.mobile.data.UuPending
import com.fufucode.mobile.data.UuRemoteProtocol
import com.fufucode.mobile.data.UuRemoteState
import org.junit.Assert.*
import org.junit.Test
import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder

class UuRemoteProtocolTest {
    private val scheme = "com.fufucode.mobile.debug.uu"
    private val protocol = UuRemoteProtocol(scheme)
    private val bind = UuRemoteState("old-device", UuPending("bind", "bind-nonce"))
    private val control = UuRemoteState("old-device", UuPending("control", "control-nonce", "old-device"))
    private fun e(s: String) = URLEncoder.encode(s, "UTF-8")
    private fun binding(tail: String) = "$scheme://bindresult?nonce=bind-nonce&$tail"
    private fun controlling(tail: String) = "$scheme://controlresult?request_id=control-nonce&$tail"
    private fun query(url: String) = URI(url).rawQuery.split("&").associate {
        val parts = it.split("=", limit = 2)
        parts[0] to URLDecoder.decode(parts[1], "UTF-8")
    }

    @Test fun bindingRoundTripPreservesOpaqueId() {
        val id = "opaque:+/= &中文"
        val result = protocol.receive(binding("anonymous_device_id=${e(id)}"), bind)!!
        assertEquals(id, result.state.deviceId)
        assertNull(result.state.pending)
        assertNull(protocol.receive(binding("anonymous_device_id=${e(id)}"), result.state))
    }

    @Test fun bindingFailurePreservesPreviousDevice() {
        for (error in listOf("no_device", "user_cancel", "timeout", "not_logined", "superseded", "logout", "internal_error", "future_error")) {
            val result = protocol.receive(binding("error=$error"), bind)!!
            assertEquals("old-device", result.state.deviceId)
            assertNull(result.state.pending)
            assertTrue(result.message.isNotBlank())
        }
    }

    @Test fun rejectsUnsolicitedStaleAndDuplicateCallbacks() {
        val url = binding("anonymous_device_id=new")
        assertNull(protocol.receive(url, UuRemoteState()))
        assertNull(protocol.receive(url.replace("bind-nonce", "old-nonce"), bind))
        assertNull(protocol.receive("$url&nonce=bind-nonce", bind))
        assertNull(protocol.receive("$url&error=user_cancel", bind))
        assertNull(protocol.receive(binding("anonymous_device_id="), bind))
        assertNull(protocol.receive(binding("error="), bind))
    }

    @Test fun rejectsWrongSchemeHostPathAndMalformedEncoding() {
        val url = binding("anonymous_device_id=new")
        for (bad in listOf(
            url.replace(scheme, "other"),
            url.replace("bindresult", "wrong"),
            url.replace("bindresult?", "bindresult/path?"),
            url.replace("bindresult?", "bindresult:80?"),
            url.replace("bindresult?", "user@bindresult?"),
            "$url#fragment",
            binding("anonymous_device_id=%Q0"),
            binding("anonymous_device_id=%00"),
        )) assertNull(bad, protocol.receive(bad, bind))
    }

    @Test fun requestUrlsEncodeCallbackAndDeviceExactlyOnce() {
        val bindQuery = query(protocol.bindUrl(bind.pending!!))
        assertEquals("$scheme://bindresult", bindQuery["callback"])
        assertEquals("bind-nonce", bindQuery["nonce"])
        val id = "opaque+/= &"
        val args = query(protocol.controlUrl(id, control.pending))
        assertEquals(id, args["anonymous_device_id"])
        assertEquals("1", args["window_type"])
        assertEquals("$scheme://controlresult?request_id=control-nonce", args["callback"])
    }

    @Test fun fullscreenNeverRequestsOrAcceptsResult() {
        val args = query(protocol.controlUrl("id", null))
        assertEquals("0", args["window_type"])
        assertFalse(args.containsKey("callback"))
        assertNull(protocol.receive(controlling("status=success"), UuRemoteState("id")))
    }

    @Test fun smallWindowSuccessConsumesPending() {
        val result = protocol.receive(controlling("status=success"), control)!!
        assertEquals("old-device", result.state.deviceId)
        assertNull(result.state.pending)
        assertNull(protocol.receive(controlling("status=success"), result.state))
    }

    @Test fun missingDeviceInvalidatesOnlyMatchedBinding() {
        assertNull(protocol.receive(controlling("status=failure&reason=device_not_found"), control.copy(deviceId = "new")))
        assertNull(protocol.receive(controlling("status=failure&reason=device_not_found").replace("control-nonce", "old"), control))
        val result = protocol.receive(controlling("status=failure&reason=device_not_found"), control)!!
        assertNull(result.state.deviceId)
        assertNull(result.state.pending)
    }

    @Test fun failuresDoNotDiscardDeviceOrPretendSuccess() {
        for (reason in listOf("busy", "device_offline", "pip_unsupported", "login_timeout", "network_unavailable", "not_allowed", "connect_timeout", "connect_failed", "user_cancel", "invalid_request", "internal_error", "future_error")) {
            val result = protocol.receive(controlling("status=failure&reason=$reason"), control)!!
            assertEquals("old-device", result.state.deviceId)
            assertNull(result.state.pending)
            assertFalse(result.message.contains("已确认"))
        }
    }

    @Test fun rejectsAmbiguousLegacyAndPartialResults() {
        for (tail in listOf("success=1", "status=other", "status=failure", "status=failure&reason=", "status=success&reason=busy", "status=success&status=failure")) {
            assertNull(protocol.receive(controlling(tail), control))
        }
        assertNull(protocol.receive(binding("anonymous_device_id=new"), control))
        assertNull(protocol.receive(controlling("status=success"), bind))
    }

    @Test fun cancelledWaitCannotBeRevivedByLateCallback() {
        assertNull(protocol.receive(controlling("status=success"), control.copy(pending = null)))
        assertNull(protocol.receive(binding("anonymous_device_id=new"), bind.copy(pending = null)))
    }

    @Test fun newRequestsHaveDifferentIdentities() {
        assertNotEquals(protocol.newBinding().nonce, protocol.newBinding().nonce)
        assertNotEquals(protocol.newControl("id").nonce, protocol.newControl("id").nonce)
    }
}
