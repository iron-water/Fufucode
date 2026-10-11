/**
 * Local OpenAI-compatible stub model.
 *
 * Pi is pointed at this server via an isolated models.json (openai-completions
 * compat). It replays a canned script of "model" responses so a real Pi agent
 * loop emits real tool_call events with no cloud credentials.
 *
 * Wire format matches what pi-ai's openai-completions parser expects:
 *   - SSE frames `data: {...}\n\n`, terminated by `data: [DONE]\n\n`
 *   - delta.content for text, delta.tool_calls[].function.{name,arguments}
 *   - a final frame carrying finish_reason and usage (Pi sends
 *     stream_options.include_usage by default)
 *
 * The script is a JSON file: an array of steps. Each chat-completions request
 * pops one step.
 *   { "type": "text", "text": "..." }                 -> assistant text, turn ends
 *   { "type": "tool", "name": "write", "args": {...} } -> emit one tool call
 */

import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";

export function loadScript(path) {
	return JSON.parse(readFileSync(path, "utf8"));
}

export function startStubModel({ port, script }) {
	const steps = [...script];
	const requests = [];

	const server = createServer((req, res) => {
		if (!req.url.endsWith("/chat/completions")) {
			res.writeHead(404, { "content-type": "application/json" });
			res.end(JSON.stringify({ error: "not found" }));
			return;
		}
		let body = "";
		req.on("data", (chunk) => { body += chunk; });
		req.on("end", () => {
			requests.push({ url: req.url, body });
			const step = steps.shift() ?? { type: "text", text: "No further scripted response." };
			res.writeHead(200, {
				"content-type": "text/event-stream",
				"cache-control": "no-cache",
				connection: "keep-alive",
			});

			const id = `chatcmpl-${randomUUID()}`;
			const created = Math.floor(Date.now() / 1000);
			const send = (obj) => res.write(`data: ${JSON.stringify(obj)}\n\n`);

			if (step.type === "tool") {
				const args = JSON.stringify(step.args ?? {});
				const callId = `call_${randomUUID().replace(/-/g, "").slice(0, 24)}`;
				send({
					id, object: "chat.completion.chunk", created,
					choices: [{
						index: 0,
						delta: {
							role: "assistant",
							tool_calls: [{
								index: 0,
								id: callId,
								type: "function",
								function: { name: step.name, arguments: args },
							}],
						},
						finish_reason: null,
					}],
				});
				send({
					id, object: "chat.completion.chunk", created,
					choices: [{ index: 0, delta: {}, finish_reason: "tool_calls" }],
					usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
				});
			}
			else {
				const text = step.text ?? "";
				send({
					id, object: "chat.completion.chunk", created,
					choices: [{ index: 0, delta: { role: "assistant", content: text }, finish_reason: null }],
				});
				send({
					id, object: "chat.completion.chunk", created,
					choices: [{ index: 0, delta: {}, finish_reason: "stop" }],
					usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
				});
			}
			res.write("data: [DONE]\n\n");
			res.end();
		});
	});

	return new Promise((resolve) => {
		server.listen(port, "127.0.0.1", () => {
			const actualPort = server.address().port;
			resolve({
				port: actualPort,
				close: () => server.close(),
				get requests() { return requests; },
				get remaining() { return steps.length; },
			});
		});
	});
}
