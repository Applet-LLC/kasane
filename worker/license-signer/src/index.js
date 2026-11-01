// kasane のライセンス署名サーバー(Cloudflare Workers)。
//
// kasane-license-svc から、ライセンスキーと MachineGuid を受け取る。Gumroad でキーを確認し、有効なら
// ECDSA P-256 でトークンに署名して返す。署名鍵(SIGNING_KEY_JWK)はこの Worker の secret にだけ置く。
// トークンの形式は driver/kbdlayer/token.c が検証する形に合わせてある:
//   <base64url(payloadJSON)>.<base64url(sig)>
//   署名対象 = base64url(payloadJSON) の ASCII バイト列(SHA-256 で署名)、sig = r||s の 64 バイト

const VERIFY_URL = "https://api.gumroad.com/v2/licenses/verify";
const KEY_PATTERN = /^[A-Za-z0-9-]{8,128}$/;
const GUID_PATTERN = /^[A-Za-z0-9{}-]{8,64}$/;
const MAX_BODY_BYTES = 2048;

export default {
  async fetch(request, env) {
    return handle(request, env);
  },
};

export async function handle(request, env, gumroadFetch = fetch, nowMs = Date.now()) {
  const url = new URL(request.url);
  if (url.pathname !== "/v1/license") {
    return json(404, { success: false, reason: "not_found" });
  }
  if (request.method !== "POST") {
    return json(405, { success: false, reason: "method_not_allowed" });
  }

  const text = await request.text();
  if (text.length > MAX_BODY_BYTES) {
    return json(400, { success: false, reason: "bad_request", message: "request too large" });
  }
  let body;
  try {
    body = JSON.parse(text);
  } catch {
    return json(400, { success: false, reason: "bad_request", message: "invalid json" });
  }
  const { licenseKey, machineGuid, mode } = body ?? {};
  if (typeof licenseKey !== "string" || !KEY_PATTERN.test(licenseKey) ||
      typeof machineGuid !== "string" || !GUID_PATTERN.test(machineGuid) ||
      (mode !== "activate" && mode !== "refresh")) {
    return json(400, { success: false, reason: "bad_request", message: "invalid parameters" });
  }

  if (!env.GUMROAD_PRODUCT_ID || !env.SIGNING_KEY_JWK) {
    return json(500, { success: false, reason: "server_misconfigured" });
  }

  const increment = mode === "activate";
  let result = await verifyWithGumroad(
    gumroadFetch, env, env.GUMROAD_PRODUCT_ID, licenseKey, increment, toInt(env.MAX_ACTIVATIONS, 100));
  let isTrial = false;

  // 本番商品に存在しないキーだけ、トライアル商品としても照合する(activate のときだけ)。
  // Gumroad に到達できなかっただけのときは試さない。
  if (!result.ok && result.kind === "rejected" && mode === "activate" && env.TRIAL_PRODUCT_ID) {
    const trial = await verifyWithGumroad(
      gumroadFetch, env, env.TRIAL_PRODUCT_ID, licenseKey, true, toInt(env.TRIAL_MAX_ACTIVATIONS, 5));
    result = trial;
    isTrial = trial.ok;
  }

  if (!result.ok) {
    if (result.kind === "rejected") {
      return json(403, { success: false, reason: "rejected", message: result.message ?? null });
    }
    return json(502, { success: false, reason: "upstream", message: result.message ?? null });
  }

  const days = isTrial ? toInt(env.TRIAL_DAYS, 6) : toInt(env.TOKEN_EXPIRY_DAYS, 7);
  const expiresAt = Math.floor(nowMs / 1000) + days * 86400;
  const uid = String(result.userId ?? result.email ?? "unknown").slice(0, 64);
  let token;
  try {
    token = await createToken(env.SIGNING_KEY_JWK, uid, machineGuid, expiresAt);
  } catch {
    return json(500, { success: false, reason: "server_misconfigured" });
  }
  return json(200, {
    success: true,
    token,
    email: result.email ?? null,
    userId: result.userId ?? null,
    isTrial,
    expiresAt,
  });
}

async function verifyWithGumroad(gumroadFetch, env, productId, licenseKey, increment, maxActivations) {
  const form = new URLSearchParams({
    product_id: productId,
    license_key: licenseKey,
    increment_uses_count: increment ? "true" : "false",
  });
  if (env.GUMROAD_ACCESS_TOKEN) form.set("access_token", env.GUMROAD_ACCESS_TOKEN);

  let resp;
  let raw;
  try {
    resp = await gumroadFetch(VERIFY_URL, { method: "POST", body: form });
    raw = await resp.text();
  } catch {
    return { ok: false, kind: "upstream", message: null };
  }

  let parsed = null;
  try { parsed = JSON.parse(raw); } catch { /* 解析できない */ }

  // Gumroad は「キーが無効」でも 404 + {"success":false,"message":...} を返すことがある。
  // 正規の拒否として読めるものは、ステータスコードによらず「拒否」として扱う。
  if (!resp.ok) {
    if (parsed && parsed.success === false) {
      return { ok: false, kind: "rejected", message: parsed.message ?? null };
    }
    return { ok: false, kind: "upstream", message: parsed?.message ?? null };
  }
  if (!parsed) return { ok: false, kind: "upstream", message: null };
  if (parsed.success !== true || !parsed.purchase) {
    return { ok: false, kind: "rejected", message: parsed.message ?? null };
  }

  const p = parsed.purchase;
  if (p.refunded === true || p.chargebacked === true) {
    return { ok: false, kind: "rejected", message: "返金またはチャージバックにより、このライセンスキーは無効になりました。" };
  }
  // 会員資格が終了した日時。3 つとも「アクセスを止めてよい」しるしとして同じに扱う。
  if (p.subscription_ended_at ?? p.subscription_cancelled_at ?? p.subscription_failed_at) {
    return { ok: false, kind: "rejected", message: "サブスクリプションが終了しました（支払いの失敗または解約）。Gumroadでご確認ください。" };
  }
  // キー流出対策: 累計 uses が上限を超えたら拒否する。uses が返らないときは判定できないので通す。
  if (Number.isInteger(parsed.uses) && parsed.uses > maxActivations) {
    return { ok: false, kind: "rejected", message: `ライセンスキーの使用台数が上限（${maxActivations}台）を超えています（現在${parsed.uses}台）。` };
  }
  return { ok: true, email: p.email ?? null, userId: p.purchaser_id ?? p.email ?? null };
}

export async function createToken(jwkText, uid, machineGuid, expUnix) {
  const jwk = JSON.parse(jwkText);
  const key = await crypto.subtle.importKey(
    "jwk", jwk, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  // ドライバは exp と mid だけを読む。利用者の入力に由来しうる uid は、最後に置く。
  const payload = JSON.stringify({ mid: machineGuid, exp: expUnix, ver: 1, uid });
  const payloadB64 = base64url(new TextEncoder().encode(payload));
  const sig = await crypto.subtle.sign(
    { name: "ECDSA", hash: "SHA-256" }, key, new TextEncoder().encode(payloadB64));
  return payloadB64 + "." + base64url(new Uint8Array(sig));
}

export function base64url(bytes) {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

function toInt(value, fallback) {
  const n = Number.parseInt(value, 10);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

function json(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  });
}
