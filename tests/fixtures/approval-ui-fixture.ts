// Test-only RPC extension. The marker is supplied by the test and stays inside tmp/.
import { writeFileSync } from "node:fs";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

export default function (pi: ExtensionAPI) {
  pi.registerCommand("fufucode-slow-approval-fixture", {
    description: "Hold a real Pi command acknowledgement beyond the ordinary prompt deadline",
    handler: async (_args, ctx) => {
      const approved = await ctx.ui.confirm("Delayed fixture approval?", "Wait for the desktop reply", { timeout: 30000 });
      if (approved) {
        const path = process.env.FUFUCODE_APPROVAL_FIXTURE_FILE;
        if (!path) throw new Error("Fixture marker path is missing");
        writeFileSync(path, "approved");
      }
    },
  });
  pi.registerCommand("fufucode-approval-fixture", {
    description: "Exercise the RPC confirmation subprotocol without a model",
    handler: async (_args, ctx) => {
      const approved = await ctx.ui.confirm(
        "Approve fixture write?",
        "Write the test marker in the temporary workspace",
        { timeout: 1500 },
      );
      if (approved) {
        const path = process.env.FUFUCODE_APPROVAL_FIXTURE_FILE;
        if (!path) throw new Error("Fixture marker path is missing");
        writeFileSync(path, "approved");
      }
      ctx.ui.notify(`fixture finished: ${approved ? "approved" : "blocked"}`, "info");
    },
  });
}
