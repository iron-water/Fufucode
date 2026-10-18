// M0 手机原型真实交互回归：M0-R1（审批过期不得再批准）及核心交互。
// 用法：node tests/Fufucode.M0.Tests/interactive.mjs（仓库根或任意目录均可）。
// 依赖：全局 @playwright/mcp 捆绑的 playwright + 系统 Chrome（channel:'chrome'）。
// 覆盖 reviewer 复现路径：弹窗打开 → 模拟断线/重连 → 允许一次 → 断言审批未生效。

import { chromium } from "file:///C:/Users/mio/AppData/Roaming/npm/node_modules/@playwright/mcp/node_modules/playwright/index.mjs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..", "..");
const page_url = "file:///" + path.join(root, "docs/frontend-prototype/mobile/index.html").replaceAll("\\", "/");

let failed = 0;
function check(cond, name, extra) {
  if (cond) { console.log("PASS " + name); return; }
  failed++;
  console.log("FAIL " + name + (extra ? " :: " + extra : ""));
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const page = await ctx.newPage();

  const cardText = async (name) => {
    const cards = await page.$$(".tool-card");
    for (const c of cards) {
      const t = await c.innerText();
      if (t.includes(name)) return t;
    }
    return "";
  };
  const sysMsgs = async () =>
    page.$$eval(".msg.sys .bubble", els => els.map(e => e.textContent));
  // 模态页遮挡时模拟条真实点击不可达：用 DOM click 派发（与 reviewer 复现一致）
  const simClick = (sel) => page.$eval(sel, el => el.click());


  // ---- M0-R1 复现1：弹窗打开时模拟断线 → 弹窗必须关闭，审批不得再提交 ----
  await page.goto(page_url + "?scene=approval");
  check(!(await page.$eval("#approval", e => e.hidden)), "r1-approval-open");

  await simClick('[data-sim="drop"]');            // 模拟断线
  await page.waitForTimeout(150);
  check(await page.$eval("#approval", e => e.hidden), "r1-modal-closed-on-drop");
  const cardAfterDrop = await cardText("分组标题渲染");
  check(cardAfterDrop.includes("待审批") && !cardAfterDrop.includes("已完成"),
    "r1-card-stays-pending-after-drop", cardAfterDrop.slice(0, 80));
  const dropMsgs = await sysMsgs();
  check(dropMsgs.some(m => m.includes("审批已失效")), "r1-invalidate-message");

  // 断线状态下直接调用 resolveApproval 的 DOM 入口不可达（弹窗已关）；
  // 再模拟重连 + 直接点击按钮（若 DOM 残留），断言旧审批仍不可生效
  await simClick('[data-sim="restore"]');         // 重连 → 代次+1，旧审批删除
  await page.waitForTimeout(1000);
  const cardAfterReconnect = await cardText("分组标题渲染");
  check(cardAfterReconnect.includes("已失效") || cardAfterReconnect.includes("待审批"),
    "r1-card-invalidated-after-reconnect", cardAfterReconnect.slice(0, 80));

  // ---- M0-R1 复现2：复刻 reviewer 步骤——弹窗开着时触发重连，然后仍点"允许一次" ----
  await page.goto(page_url + "?scene=approval");
  await simClick('[data-sim="restore"]');         // 弹窗存在时重连
  // 弹窗应在 connecting 阶段即关闭；无论是否关，再点一次允许按钮强制提交
  const btnStillThere = await page.$eval("#btnApprove", e => !e.closest(".sheet").hidden)
    .catch(() => false);
  if (btnStillThere) await page.click("#btnApprove");
  else {
    // DOM 强制点击：即使弹窗已隐藏，调用 click() 也应被提交时复核拦截
    await page.$eval("#btnApprove", e => e.click());
  }
  await page.waitForTimeout(1200);
  const cardRace = await cardText("分组标题渲染");
  check(!cardRace.includes("已完成"), "r1-stale-approve-not-applied", cardRace.slice(0, 80));
  const raceMsgs = await sysMsgs();
  check(!raceMsgs.some(m => m === "已允许本次执行"),
    "r1-no-false-approval-message", raceMsgs.slice(-2).join("|"));

  // ---- 正常路径：允许 ----
  await page.goto(page_url + "?scene=approval");
  await page.click("#btnApprove");
  await page.waitForTimeout(200);
  const okCard = await cardText("分组标题渲染");
  check(okCard.includes("已完成"), "approve-happy-path", okCard.slice(0, 60));
  check((await sysMsgs()).some(m => m === "已允许本次执行"), "approve-message");

  // ---- 正常路径：拒绝 ----
  await page.goto(page_url + "?scene=approval");
  await page.click("#btnDeny");
  await page.waitForTimeout(200);
  const denyCard = await cardText("分组标题渲染");
  check(denyCard.includes("失败"), "deny-happy-path", denyCard.slice(0, 60));

  // ---- 宿主退出：弹窗关闭且不可重连 ----
  await page.goto(page_url + "?scene=approval");
  await simClick('[data-sim="hostExit"]');
  await page.waitForTimeout(150);
  check(await page.$eval("#approval", e => e.hidden), "hostexit-modal-closed");
  check(await page.$eval("#bannerAction", e => e.hidden), "hostexit-no-reconnect");
  check((await page.$eval("#bannerText", e => e.textContent)).includes("不能继续控制"),
    "hostexit-banner-text");

  // ---- 完整流：连接→列表→发送→停止 ----
  await page.goto(page_url);
  await page.fill("#pairCode", "426810");
  await page.click("#btnConnect");
  await page.waitForSelector("#screen-list.active", { timeout: 3000 });
  await page.click("#sessionList li .go");
  await page.fill("#input", "验证发送");
  await page.click("#btnSend");
  await page.waitForSelector("#btnStop:not([hidden])", { timeout: 2000 });
  await page.click("#btnStop");
  await page.waitForTimeout(200);
  check((await sysMsgs()).some(m => m.includes("clear_queue + abort")), "stop-message");
  check(!(await page.$eval("#btnSend", e => e.hidden)), "send-restored-after-stop");
} finally {
  await browser.close();
}

console.log(failed === 0 ? "ALL INTERACTIVE PASS" : failed + " FAILED");
process.exit(failed === 0 ? 0 : 1);
