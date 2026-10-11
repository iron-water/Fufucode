/**
 * Standalone stub-model server for installed-CLI smoke tests.
 * Serves an OpenAI-compatible /v1/chat/completions SSE stream.
 * One response step per request; script given inline below.
 *
 * Usage: node stub-server.mjs <port>
 */

import { createServer } from "node:http";
import { randomUUID } from "node:crypto";

const port = Number(process.argv[2] || 18911);

// Script: turn 1 = plain text reply (chat smoke); turn 2 = write tool_call
// (proves the approval bridge intercepts), turn 3 = text after block.
const steps = [
	{ type: "text", text: "stub round complete: fufucode-chat-ok" },
	{
		type: "tool",
		name: "write",
		args: { path: "bridge-check.txt", content: "should-not-exist-if-bridge-blocks" },
	},
	{ type: "text", text: "write was not executed (as expected under approval gate)" },
];

const server = createServer((req, res) => {
	if (!req.url.endsWith("/chat/completions")) {
		res.writeHead(404, { "content-type": "application/json" });
		res.end(JSON.stringify({ error: "not found" }));
		return;
	}
	let body = "";
	req.on("data", (c) => { body += c; });
	req.on("end", () => {
		const step = steps.shift() ?? { type: "text", text: "stub: no more steps" };
		res.writeHead(200, {
			"content-type": "text/event-stream",
			"cache-control": "no-cache",
			connection: "keep-alive",
		});
		const id = `chatcmpl-${randomUUID()}`;
		const created = Math.floor(Date.now() / 1000);
		const send = (o) => res.write(`data: ${JSON.stringify(o)}\n\n`);
		if (step.type === "tool") {
			const args = JSON.stringify(step.args ?? {});
			send({ id, object: "chat.completion.chunk", created, choices: [{ index: 0, delta: { role: "assistant", tool_calls: [{ index: 0, id: `call_${randomUUID().replace(/-/g, "").slice(0, 24)}`, type: "function", function: { name: step.name, arguments: args } }] }, finish_reason: null }] });
			send({ id, object: "chat.completion.chunk", created, choices: [{ index: 0, delta: {}, finish_reason: "tool_calls" }], usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 } });
		}
		else {
			send({ id, object: "chat.completion.chunk", created, choices: [{ index: 0, delta: { role: "assistant", content: step.text }, finish_reason: null }] });
			send({ id, object: "chat.completion.chunk", created, choices: [{ index: 0, delta: {}, finish_reason: "stop" }], usage: { prompt_tokens: 5, completion_tokens: 3, total_tokens: 8 } });
		}
		res.write("data: [DONE]\n\n");
		res.end();
	});
});

server.listen(port, "127.0.0.1", () => console.log(`stub listening on 127.0.0.1:${port}`));
