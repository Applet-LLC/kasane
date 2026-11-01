# kasane ライセンス署名サーバー

kasane の Subscription 版が使うライセンストークンに署名するサーバーです。Cloudflare Workers で動かします。

`kasane-license-svc` は、ライセンスキーとパソコンの MachineGuid をこのサーバーに送ります。サーバーは Gumroad でキーを確認し、有効ならトークンに署名して返します。署名鍵はこのサーバーの secret にだけ置きます。インストーラーにも、リポジトリにも入りません。

## 動き

`POST /v1/license` に JSON を送ります。

```json
{ "licenseKey": "XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX", "machineGuid": "…", "mode": "activate" }
```

- `mode` は `activate`（アクティベート）か `refresh`（定期の再確認）です。`activate` のときだけ、Gumroad の利用回数を増やします。本番商品に無いキーは、トライアル商品としても確認します。
- 有効なら 200 で `token`（署名済み）、`expiresAt`、`isTrial` などを返します。
- Gumroad が明確に拒否したとき（返金、解約、台数超過など）は 403 です。
- Gumroad に届かないとき、応答を読めないときは 502 です。クライアントは、ライセンスを取り消さずに再試行します。

トークンの形式と署名の対象は `driver/kbdlayer/token.c` が検証するものと同じです。

## 設定

| 名前 | 種類 | 内容 |
|---|---|---|
| `SIGNING_KEY_JWK` | secret | 署名鍵（ECDSA P-256 の秘密鍵、JWK 形式） |
| `GUMROAD_PRODUCT_ID` | secret | Subscription 版の Gumroad の product_id |
| `TRIAL_PRODUCT_ID` | secret | 評価版の product_id。空ならトライアルは無効 |
| `GUMROAD_ACCESS_TOKEN` | secret | 任意。非公開の商品のときだけ |
| `MAX_ACTIVATIONS` ほか | 変数 | `wrangler.toml` にある（100、評価版は 5、有効期限は 7 日、評価版は 6 日） |

## 初めてのデプロイ

1. Cloudflare のアカウントを作ります（無料のプランで足ります）。
2. このフォルダで `npx wrangler login` を実行し、続けて `npx wrangler deploy` を実行します。表示された URL（`https://kasane-license.<アカウント名>.workers.dev`）を控えます。
3. 署名鍵を作ります。出力先はリポジトリの外にしてください。

   ```powershell
   node tools/gen-key.mjs C:\path\outside\repo\signing-key.jwk
   ```

   表示された公開鍵を、`driver/kbdlayer/token.c` の `s_PubKeyBlob` に貼ります。ドライバを作り直して、署名します。
4. secret を入れます。

   ```powershell
   Get-Content C:\path\outside\repo\signing-key.jwk -Raw | npx wrangler secret put SIGNING_KEY_JWK
   npx wrangler secret put GUMROAD_PRODUCT_ID
   npx wrangler secret put TRIAL_PRODUCT_ID
   ```
5. `kasane-license-svc/LicenseServerClient.cs` の `DefaultUrl` に、手順 2 の URL を書きます。
6. 動作を確かめます。有効なキーで 200、でたらめなキーで 403 になることを見ます。
7. Cloudflare のダッシュボードで、このパスにレート制限をかけます（例: 同じ IP から 1 分に 10 回まで）。

署名鍵のファイルは、別の場所にバックアップしてください。失うと、鍵を作り直してドライバを再配布することになります。

## 鍵を変えるとき

新しい鍵を `tools/gen-key.mjs` で作り、`token.c` の公開鍵を差し替えてドライバを再配布します。サーバーの secret は、ドライバの配布が済んでから切り替えます。古いドライバと新しいドライバの両方に対応する期間は、設計していません。

## テスト

```powershell
npm test
```

Node.js 20 以降が必要です。Gumroad への通信は、テストの中で模擬しています。
