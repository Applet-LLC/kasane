import test from "node:test";
import assert from "node:assert/strict";
import { handle } from "../src/index.js";

const KEY = "ABCD1234-ABCD1234-ABCD1234-ABCD1234";
const MID = "0b1f6c1e-2f3a-4c55-9d3e-5a1b2c3d4e5f";
const NOW = 1_800_000_000_000;

const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const env = {
  GUMROAD_PRODUCT_ID: "PROD",
  TRIAL_PRODUCT_ID: "TRIAL",
  SIGNING_KEY_JWK: JSON.stringify(await crypto.subtle.exportKey("jwk", pair.privateKey)),
};

function req(body, { method = "POST", path = "/v1/license" } = {}) {
  return new Request("https://example.test" + path, {
    method,
    body: method === "POST" ? (typeof body === "string" ? body : JSON.stringify(body)) : undefined,
  });
}
const good = { licenseKey: KEY, machineGuid: MID, mode: "activate" };

// product_id ごとに応答を返す Gumroad の模擬。calls に呼ばれた内容を残す。
function gumroad(byProduct) {
  const calls = [];
  const fn = async (_url, init) => {
    const f = Object.fromEntries(init.body);
    calls.push(f);
    const r = byProduct[f.product_id];
    return new Response(JSON.stringify(r.body), { status: r.status ?? 200 });
  };
  fn.calls = calls;
  return fn;
}
const okBody = (extra = {}, uses = 1) => ({
  success: true, uses, purchase: { email: "a@example.com", purchaser_id: "p123", ...extra },
});
const b64uToBytes = (s) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));

async function verifies(token) {
  const [p, s] = token.split(".");
  return crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, pair.publicKey,
    b64uToBytes(s), new TextEncoder().encode(p));
}

test("有効なキーに、検証できる署名つきのトークンを返す", async () => {
  const g = gumroad({ PROD: { body: okBody() } });
  const res = await handle(req(good), env, g, NOW);
  assert.equal(res.status, 200);
  const j = await res.json();
  assert.equal(j.success, true);
  assert.equal(j.isTrial, false);
  assert.equal(j.expiresAt, NOW / 1000 + 7 * 86400);
  assert.equal(await verifies(j.token), true);
  const payload = JSON.parse(new TextDecoder().decode(b64uToBytes(j.token.split(".")[0])));
  assert.deepEqual(payload, { mid: MID, exp: j.expiresAt, ver: 1, uid: "p123" });
  assert.equal(b64uToBytes(j.token.split(".")[1]).length, 64);
  assert.equal(g.calls[0].increment_uses_count, "true");
});

test("改ざんしたトークンは検証に通らない", async () => {
  const j = await (await handle(req(good), env, gumroad({ PROD: { body: okBody() } }), NOW)).json();
  const [p, s] = j.token.split(".");
  const tampered = p.slice(0, -2) + (p.endsWith("AA") ? "BB" : "AA") + "." + s;
  assert.equal(await verifies(tampered), false);
});

test("refresh では利用回数を増やさず、トライアルを試さない", async () => {
  const g = gumroad({ PROD: { body: { success: false, message: "nope" }, status: 404 }, TRIAL: { body: okBody() } });
  const res = await handle(req({ ...good, mode: "refresh" }), env, g, NOW);
  assert.equal(res.status, 403);
  assert.equal(g.calls.length, 1);
  assert.equal(g.calls[0].increment_uses_count, "false");
});

test("本番商品に無いキーは、activate のときだけトライアル商品で照合する", async () => {
  const g = gumroad({ PROD: { body: { success: false, message: "x" }, status: 404 }, TRIAL: { body: okBody({}, 1) } });
  const j = await (await handle(req(good), env, g, NOW)).json();
  assert.equal(j.success, true);
  assert.equal(j.isTrial, true);
  assert.equal(j.expiresAt, NOW / 1000 + 6 * 86400);
  assert.equal(await verifies(j.token), true);
});

test("トライアルの使用回数が上限(5)を超えたら拒否する", async () => {
  const g = gumroad({ PROD: { body: { success: false }, status: 404 }, TRIAL: { body: okBody({}, 6) } });
  const res = await handle(req(good), env, g, NOW);
  assert.equal(res.status, 403);
  assert.match((await res.json()).message, /上限（5台）/);
});

test("返金・チャージバック・サブスク終了・使用台数超過は拒否する", async () => {
  const cases = [
    [okBody({ refunded: true }), /返金/],
    [okBody({ chargebacked: true }), /返金/],
    [okBody({ subscription_cancelled_at: "2026-01-01" }), /サブスクリプションが終了/],
    [okBody({ subscription_failed_at: "2026-01-01" }), /サブスクリプションが終了/],
    [okBody({}, 101), /上限（100台）/],
  ];
  for (const [body, re] of cases) {
    const res = await handle(req(good), { ...env, TRIAL_PRODUCT_ID: "" }, gumroad({ PROD: { body } }), NOW);
    assert.equal(res.status, 403);
    assert.match((await res.json()).message, re);
  }
});

test("Gumroad に届かない・読めない応答は 502(一時的な失敗)", async () => {
  const down = async () => { throw new TypeError("network"); };
  assert.equal((await handle(req(good), env, down, NOW)).status, 502);
  const bad = async () => new Response("<html>", { status: 500 });
  const res = await handle(req(good), env, bad, NOW);
  assert.equal(res.status, 502);
  assert.equal((await res.json()).reason, "upstream");
});

test("不正な入力は 400、メソッドとパスの違いは 405 と 404", async () => {
  const g = gumroad({ PROD: { body: okBody() } });
  for (const body of [
    { ...good, licenseKey: "short" },
    { ...good, licenseKey: "ABCD 1234 ABCD" },
    { ...good, machineGuid: "x" },
    { ...good, mode: "other" },
    "not json",
  ]) {
    assert.equal((await handle(req(body), env, g, NOW)).status, 400);
  }
  assert.equal(g.calls.length, 0);
  assert.equal((await handle(req(null, { method: "GET" }), env, g, NOW)).status, 405);
  assert.equal((await handle(req(good, { path: "/" }), env, g, NOW)).status, 404);
});

test("secret が無い、または署名鍵が壊れているときは 500", async () => {
  const g = gumroad({ PROD: { body: okBody() } });
  assert.equal((await handle(req(good), { ...env, SIGNING_KEY_JWK: "" }, g, NOW)).status, 500);
  assert.equal((await handle(req(good), { ...env, SIGNING_KEY_JWK: "{}" }, g, NOW)).status, 500);
});
