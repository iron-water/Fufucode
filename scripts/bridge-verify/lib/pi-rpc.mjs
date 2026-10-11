/**
 * Minimal Pi RPC client: speaks the JSON-lines protocol over stdio and
 * auto-answers extension_ui_request dialogs per a scriptable policy.
 */

import { spawn } from "node:child_process";
import { EventEmitter } from "node:events";

export class PiRpc extends EventEmitter {
	constructor({ nodeExe, cliJs, args = [], env = {}, cwd }) {
		super();
		this.child = spawn(nodeExe, [cliJs, ...args], {
			cwd,
			env: { ...process.env, ...env },
			stdio: ["pipe", "pipe", "pipe"],
			shell: false,
			windowsHide: true,
		});
		this._nextId = 1;
		this._pending = new Map();
		this._buffer = "";
		this.dead = false;
		this.uiRequests = [];
		this.stderrText = "";
		this._uiPolicy = () => ({ confirmed: true });

		const onLine = (data) => {
			this._buffer += data.toString("utf8");
			let idx;
			while ((idx = this._buffer.indexOf("\n")) >= 0) {
				const raw = this._buffer.slice(0, idx).trim();
				this._buffer = this._buffer.slice(idx + 1);
				if (!raw) continue;
				let frame;
				try {
					frame = JSON.parse(raw);
				}
				catch {
					this.emit("malformed", raw);
					continue;
				}
				this._handleFrame(frame);
			}
		};

		this.child.stdout.setEncoding("utf8");
		this.child.stdout.on("data", onLine);
		this.child.stderr.on("data", (d) => {
			this.stderrText += d.toString("utf8");
			this.emit("stderr", d.toString("utf8"));
		});
		this.child.on("exit", (code, signal) => {
			this.dead = true;
			this.emit("exit", { code, signal });
			for (const { reject } of this._pending.values()) {
				reject(new Error(`pi exited (code=${code} signal=${signal})`));
			}
			this._pending.clear();
		});
	}

	setUiPolicy(policy) {
		this._uiPolicy = policy;
	}

	_handleFrame(frame) {
		if (frame.type === "response") {
			const pending = this._pending.get(frame.id);
			if (pending) {
				this._pending.delete(frame.id);
				if (frame.success) pending.resolve(frame.data);
				else pending.reject(new Error(frame.error ?? "rpc error"));
				return;
			}
		}
		if (frame.type === "extension_ui_request") {
			this.uiRequests.push(frame);
			this.emit("ui_request", frame);
			this.emit("event", frame);
			const answer = this._uiPolicy(frame);
			if (answer) this.answerUi(frame.id, answer);
			return;
		}
		this.emit("event", frame);
		if (frame.type) this.emit(frame.type, frame);
	}

	answerUi(id, answer) {
		this._write({ type: "extension_ui_response", id, ...answer });
	}

	_write(obj) {
		if (this.dead) throw new Error("pi is dead");
		this.child.stdin.write(JSON.stringify(obj) + "\n");
	}

	send(command, data = {}, timeoutMs = 20000) {
		const id = this._nextId++;
		return new Promise((resolve, reject) => {
			const timer = setTimeout(() => {
				if (this._pending.has(id)) {
					this._pending.delete(id);
					reject(new Error(`timeout waiting for '${command}' response`));
				}
			}, timeoutMs);
			this._pending.set(id, {
				resolve: (v) => { clearTimeout(timer); resolve(v); },
				reject: (e) => { clearTimeout(timer); reject(e); },
			});
			this._write({ id, type: command, ...data });
		});
	}

	async waitForEvent(predicate, timeoutMs = 60000) {
		return new Promise((resolve, reject) => {
			const timer = setTimeout(() => {
				this.off("event", check);
				reject(new Error("timeout waiting for event"));
			}, timeoutMs);
			const check = (e) => {
				if (predicate(e)) {
					clearTimeout(timer);
					this.off("event", check);
					resolve(e);
				}
			};
			this.on("event", check);
		});
	}

	async collectUntil(predicate, timeoutMs = 60000) {
		const events = [];
		const done = this.waitForEvent(predicate, timeoutMs);
		const onEvent = (e) => events.push(e);
		this.on("event", onEvent);
		try {
			await done;
			return events;
		}
		finally {
			this.off("event", onEvent);
		}
	}

	kill() {
		if (!this.dead) {
			try { this.child.kill(); } catch { /* ignore */ }
		}
	}
}
