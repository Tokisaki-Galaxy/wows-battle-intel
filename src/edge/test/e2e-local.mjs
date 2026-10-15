/**
 * 协作缓存 v1 本地端到端验证。
 *
 * 用 unstable_dev 拉起本地 worker（miniflare local，持久化到 .wrangler/state，
 * 与 wrangler d1 migrations apply --local 共享同一份 D1），全流程走 HTTP：
 * 注册 → 查询签发 token → 挖时隙票据 → 回传 → 命中复查 → 三类拒绝 → 管理观察面。
 *
 * 运行：在 src/edge 目录下 node test/e2e-local.mjs
 * 退出码：0 全部通过，1 存在失败。
 */
import { unstable_dev } from "wrangler";
import crypto from "node:crypto";

/** 每步记录：步骤号、描述、通过与否、关键明细。 */
const records = [];
let failed = false;

function record(step, desc, pass, detail = "") {
  records.push({ step, desc, pass, detail });
  if (!pass) failed = true;
  console.log(`${pass ? "PASS" : "FAIL"}  [${step}] ${desc}${detail ? ` :: ${detail}` : ""}`);
}

/** 90 秒看门狗：任何环节卡死（含 worker.stop 挂起）都强制退出非零。 */
const watchdog = setTimeout(() => {
  console.error("FAIL  看门狗触发：90 秒未完成，强制退出");
  process.exit(1);
}, 90_000);

/**
 * 挖时隙票据：SHA256(deviceId + secretHash + slot + nonce) 前 difficulty bit 为零。
 * 与 src/edge/src/lib/verify.ts 的 verifySlotTicketWithHash 同一口径（逐字节前导零）。
 */
function mineTicket(deviceId, secretHash, slot, difficulty) {
  const prefix = `${deviceId}${secretHash}${slot}`;
  for (let nonce = 0; nonce < 100_000_000; nonce++) {
    const digest = crypto.createHash("sha256").update(prefix + nonce).digest();
    let zeroBits = 0;
    for (const byte of digest) {
      if (byte === 0) {
        zeroBits += 8;
        continue;
      }
      zeroBits += Math.clz32(byte) - 24;
      break;
    }
    if (zeroBits >= difficulty) {
      return { nonce: String(nonce), zeroBits, tries: nonce + 1 };
    }
  }
  throw new Error("挖矿未收敛：1 亿次哈希内未找到满足难度的 nonce");
}

let worker;
try {
  worker = await unstable_dev("src/index.ts", {
    local: true,
    persistTo: ".wrangler/state",
    // wrangler 4.151.0 的 Unstable_DevOptions 顶层支持 vars，直接注入 Env 必填项
    vars: {
      REPORT_SALT: "e2e-salt",
      ADMIN_TOKEN: "e2e-admin",
    },
    logLevel: "error",
    experimental: {
      disableExperimentalWarning: true,
      watch: false,
    },
  });
  console.log(`worker 已启动：http://127.0.0.1:${worker.port}`);

  const base = `http://127.0.0.1:${worker.port}`;
  /** 统一 HTTP 入口：返回 { status, body }，body 尽力解析 JSON。 */
  async function http(method, path, { headers = {}, body } = {}) {
    const res = await fetch(base + path, {
      method,
      headers: body !== undefined ? { "content-type": "application/json", ...headers } : headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json = null;
    try {
      json = JSON.parse(text);
    } catch {
      // 非 JSON 响应保留原文片段
    }
    return { status: res.status, json, text };
  }

  // ── 步骤 1：设备注册 ──────────────────────────────────────────────
  const reg1 = await http("POST", "/api/v1/devices/register", { body: {} });
  const cred = reg1.json?.data ?? {};
  record(
    1,
    "设备注册返回凭证",
    reg1.status === 200 &&
      reg1.json?.ok === true &&
      typeof cred.deviceId === "string" &&
      typeof cred.secret === "string" &&
      /^[0-9a-f]{64}$/.test(cred.secretHash ?? "") &&
      cred.difficulty === 16 &&
      cred.slotSeconds === 300,
    `status=${reg1.status} difficulty=${cred.difficulty} slotSeconds=${cred.slotSeconds} secretHashLen=${(cred.secretHash ?? "").length}`,
  );

  // ── 步骤 2：幂等重注册 ───────────────────────────────────────────
  const reg2 = await http("POST", "/api/v1/devices/register", {
    body: { deviceId: cred.deviceId, secret: cred.secret },
  });
  record(
    2,
    "同 deviceId+secret 重注册返回相同 deviceId",
    reg2.status === 200 && reg2.json?.data?.deviceId === cred.deviceId,
    `status=${reg2.status} deviceIdMatch=${reg2.json?.data?.deviceId === cred.deviceId}`,
  );

  // ── 步骤 3：批量查询，两个未命中键 ───────────────────────────────
  // 每次运行随机生成纯数字账号键：.wrangler/state 持久化跨运行共享，
  // 固定键会被上一轮的回传污染成命中，无法验证未命中签发链路。
  const keyA = String(1_000_000_000 + Math.floor(Math.random() * 8_000_000_000));
  const keyB = String(1_000_000_000 + Math.floor(Math.random() * 8_000_000_000));
  const queryBody = {
    deviceId: cred.deviceId,
    accounts: [
      { accountId: keyA, server: "ASIA" },
      { accountId: keyB, server: "ASIA" },
    ],
  };
  const q1 = await http("POST", "/api/v1/players/query", { body: queryBody });
  const q1d = q1.json?.data ?? {};
  const token1 = q1d.missingToken ?? "";
  record(
    3,
    "首次查询 hits 空、missing 两项、missingToken 存在",
    q1.status === 200 &&
      Array.isArray(q1d.hits) && q1d.hits.length === 0 &&
      Array.isArray(q1d.missing) && q1d.missing.length === 2 &&
      typeof token1 === "string" && token1.length > 0,
    `status=${q1.status} hits=${q1d.hits?.length} missing=${q1d.missing?.length} tokenLen=${token1.length}`,
  );

  // ── 步骤 4：挖 12 bit 时隙票据 ───────────────────────────────────
  const slot = Math.floor(Date.now() / 1000 / 300);
  const t0 = Date.now();
  const ticket1 = mineTicket(cred.deviceId, cred.secretHash, slot, 12);
  console.log(
    `      挖矿：slot=${slot} nonce=${ticket1.nonce} 前导零=${ticket1.zeroBits}bit 尝试=${ticket1.tries}次 耗时=${Date.now() - t0}ms`,
  );
  record(4, "挖到 12 bit 前导零票据", ticket1.zeroBits >= 12, `nonce=${ticket1.nonce} tries=${ticket1.tries}`);

  // ── 步骤 5：回传两行合法数据 ─────────────────────────────────────
  const rows = [
    {
      accountId: keyA, server: "ASIA", shipId: "",
      payload: { accountId: keyA, server: "ASIA", accountBattles: 5000, accountWinrate: 0.52, recent20Winrate: 0.55, updatedAt: Date.now() },
    },
    {
      accountId: keyB, server: "ASIA", shipId: "",
      payload: { accountId: keyB, server: "ASIA", accountBattles: 5000, accountWinrate: 0.52, recent20Winrate: 0.55, updatedAt: Date.now() },
    },
  ];
  const rep1 = await http("POST", "/api/v1/players/report", {
    body: {
      deviceId: cred.deviceId,
      slot,
      nonce: ticket1.nonce,
      missingToken: token1,
      rows,
    },
  });
  record(
    5,
    "回传成功 accepted=2",
    rep1.status === 200 && rep1.json?.ok === true && rep1.json?.data?.accepted === 2,
    `status=${rep1.status} accepted=${rep1.json?.data?.accepted} error=${rep1.json?.error ?? "-"}`,
  );

  // ── 步骤 6：复查命中两条 ─────────────────────────────────────────
  const q2 = await http("POST", "/api/v1/players/query", { body: queryBody });
  const q2d = q2.json?.data ?? {};
  const hitKeys = (q2d.hits ?? []).map((h) => h.accountId).sort().join(",");
  record(
    6,
    "复查命中 2 条",
    q2.status === 200 && (q2d.hits?.length ?? 0) === 2 && hitKeys === [keyA, keyB].sort().join(","),
    `status=${q2.status} hits=${q2d.hits?.length} keys=${hitKeys}`,
  );

  // ── 步骤 7：同一 slot 再回传 → 403 E_SLOT ───────────────────────
  const ticket2 = mineTicket(cred.deviceId, cred.secretHash, slot, 12);
  const rep2 = await http("POST", "/api/v1/players/report", {
    body: { deviceId: cred.deviceId, slot, nonce: ticket2.nonce, missingToken: token1, rows },
  });
  record(
    7,
    "同时隙重复回传被拒 E_SLOT",
    rep2.status === 403 && (rep2.json?.error ?? "").startsWith("E_SLOT"),
    `status=${rep2.status} error=${rep2.json?.error ?? rep2.text.slice(0, 80)}`,
  );

  // ── 步骤 8：伪造 token → 403 E_TOKEN ────────────────────────────
  // 防线顺序是 E_AUTH → E_SLOT → E_TOKEN：步骤 7 已占住本设备当前时隙，
  // 同设备同 slot 的后续请求先撞 E_SLOT，到不了 token 校验。
  // 注册第二个设备拿到未占用的 (deviceId, slot) 组合再测。
  const reg3 = await http("POST", "/api/v1/devices/register", { body: {} });
  const cred2 = reg3.json?.data ?? {};
  record(
    "7b",
    "注册第二设备用于 E_TOKEN 与 E_RANGE 阶段",
    reg3.status === 200 && typeof cred2.deviceId === "string" && cred2.deviceId !== cred.deviceId,
    `status=${reg3.status} deviceId=${cred2.deviceId}`,
  );

  let forgeStatus = 0;
  let forgeOk = true;
  const forgeTicket = mineTicket(cred2.deviceId, cred2.secretHash, slot, 12);
  for (let i = 0; i < 60; i++) {
    const repForge = await http("POST", "/api/v1/players/report", {
      body: {
        deviceId: cred2.deviceId,
        slot,
        nonce: forgeTicket.nonce,
        missingToken: "f".repeat(32),
        rows,
      },
    });
    forgeStatus = repForge.status;
    if (repForge.status !== 403 || !(repForge.json?.error ?? "").startsWith("E_TOKEN")) {
      forgeOk = false;
      console.log(`      第 ${i} 次伪造返回异常：${repForge.status} ${repForge.json?.error ?? repForge.text.slice(0, 80)}`);
      break;
    }
  }
  record(8, "伪造 token 被拒 E_TOKEN（60 次）", forgeOk && forgeStatus === 403, `lastStatus=${forgeStatus}`);

  // ── 步骤 9：合法 token + 胜率 1.5 → 400 E_RANGE ─────────────────
  // token 一次性，E_TOKEN 阶段的拒绝不写时隙占位，设备二当前时隙仍可用。
  // 查询一个全新键制造 miss 并签发新 token。
  const q4 = await http("POST", "/api/v1/players/query", {
    body: {
      deviceId: cred2.deviceId,
      accounts: [
        { accountId: "4003", server: "ASIA" },
      ],
    },
  });
  const token4 = q4.json?.data?.missingToken ?? "";
  record(
    "9a",
    "设备二查询新键 4003 签发新 token",
    q4.status === 200 && (q4.json?.data?.missing?.length ?? 0) === 1 && token4.length > 0,
    `status=${q4.status} missing=${q4.json?.data?.missing?.length} tokenLen=${token4.length}`,
  );
  const ticket3 = mineTicket(cred2.deviceId, cred2.secretHash, slot, 12);
  const repRange = await http("POST", "/api/v1/players/report", {
    body: {
      deviceId: cred2.deviceId,
      slot,
      nonce: ticket3.nonce,
      missingToken: token4,
      rows: [
        {
          accountId: "4003", server: "ASIA", shipId: "",
          payload: { accountId: "4003", server: "ASIA", accountBattles: 5000, accountWinrate: 1.5, recent20Winrate: 0.55, updatedAt: Date.now() },
        },
      ],
    },
  });
  record(
    9,
    "胜率 1.5 触发 E_RANGE 400",
    repRange.status === 400 && (repRange.json?.error ?? "").startsWith("E_RANGE"),
    `status=${repRange.status} error=${repRange.json?.error ?? repRange.text.slice(0, 80)}`,
  );

  // ── 步骤 10：健康检查与管理观察面 ────────────────────────────────
  const health = await http("GET", "/api/health");
  record(
    "10a",
    "健康检查 200",
    health.status === 200 && health.json?.ok === true,
    `status=${health.status}`,
  );

  const adminOk = await http("GET", "/api/v1/admin/rejections", {
    headers: { authorization: "Bearer e2e-admin" },
  });
  const rejCount = adminOk.json?.data?.rejections?.length ?? -1;
  record(
    "10b",
    "带 Bearer 查拒绝日志 200 且非空",
    adminOk.status === 200 && Array.isArray(adminOk.json?.data?.rejections) && rejCount > 0,
    `status=${adminOk.status} rejections=${rejCount} codes=${(adminOk.json?.data?.rejections ?? []).map((r) => r.code).join(",")}`,
  );

  const adminNo = await http("GET", "/api/v1/admin/rejections");
  record(
    "10c",
    "无 token 查拒绝日志 401",
    adminNo.status === 401,
    `status=${adminNo.status}`,
  );
} catch (error) {
  failed = true;
  console.error("FAIL  脚本执行异常：");
  console.error(error?.stack ?? String(error));
} finally {
  clearTimeout(watchdog);
  if (worker) {
    try {
      await worker.stop();
      console.log("worker 已停止");
    } catch (error) {
      console.error(`worker.stop 异常：${error?.message ?? error}`);
    }
  }
}

const passCount = records.filter((r) => r.pass).length;
console.log("───────");
console.log(`总结：${passCount}/${records.length} 项通过，整体 ${failed ? "FAIL" : "PASS"}`);
process.exit(failed ? 1 : 0);
