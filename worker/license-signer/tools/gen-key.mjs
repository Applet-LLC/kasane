// 署名鍵ペア(ECDSA P-256)を作る。
//   node tools/gen-key.mjs <秘密鍵(JWK)の出力先>
// 秘密鍵は指定したファイルにだけ書く(リポジトリの外を指定すること。上書きはしない)。
// 画面には、driver/kbdlayer/token.c の s_PubKeyBlob に貼る公開鍵だけを出す。
import { writeFileSync } from "node:fs";

const out = process.argv[2];
if (!out) {
  console.error("usage: node tools/gen-key.mjs <output.jwk>");
  process.exit(2);
}
const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const jwk = await crypto.subtle.exportKey("jwk", pair.privateKey);
writeFileSync(out, JSON.stringify(jwk), { flag: "wx" });

const fromB64Url = (s) => Buffer.from(s.replace(/-/g, "+").replace(/_/g, "/"), "base64");
const x = fromB64Url(jwk.x);
const y = fromB64Url(jwk.y);
const blob = Buffer.concat([Buffer.from([0x45, 0x43, 0x53, 0x31, 0x20, 0, 0, 0]), x, y]);
const rows = [];
for (let i = 0; i < blob.length; i += 8) {
  rows.push("    " + [...blob.subarray(i, i + 8)].map((v) => "0x" + v.toString(16).toUpperCase().padStart(2, "0")).join(", ") + ",");
}
console.log("public key (BCRYPT_ECCPUBLIC_BLOB, 72 bytes):");
console.log(rows.join("\n"));
