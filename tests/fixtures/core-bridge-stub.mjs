import { readFileSync } from "node:fs";
import { startStubModel } from "../../scripts/bridge-verify/lib/stub-model.mjs";

const script = JSON.parse(readFileSync(process.argv[2], "utf8"));
const stub = await startStubModel({ port: 0, script });
process.stdout.write(`${stub.port}\n`);
process.stdin.resume();
process.stdin.on("end", () => stub.close());
