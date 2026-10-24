using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KasaneLicenseSvc;

/// <summary>
/// ECDSA P-256 でライセンストークンを生成する。
/// トークン形式: &lt;base64url(payloadJSON)&gt;.&lt;base64url(sig_r_s)&gt;
///   payloadJSON : {"uid":"...","mid":"...","exp":unix_ts,"ver":1}
///   署名対象    : SHA-256(base64url(payloadJSON) の ASCII バイト列)
///   署名形式    : IEEE P1363 raw r||s (64 bytes)
///
/// 公開鍵は kbdlayer.sys の token.c に埋め込む（driver/kbdlayer/KEYS.md 参照）。
///
/// 秘密鍵の扱い（kbdaddid-license-svcのDPAPI移管方式をそのまま踏襲）:
/// 秘密鍵の生の値はソースにもgit管理下のファイルにも置かない:
///   1. appsettings.json はgitignore対象。appsettings.Sample.json がテンプレート
///      （driver/kbdlayer/KEYS.md 参照）。
///   2. 初回起動時のみ appsettings.json の SigningKey セクションから読み込み、
///      DPAPI（ProtectedData, LocalMachine スコープ）で暗号化して
///      %ProgramData%\kbdlayer-license\license-svc\signingkey.dat に保存する。
///   3. 保存が成功したら、appsettings.json 自身から SigningKey セクションを削除する
///      （平文の鍵がインストールフォルダに残り続ける期間を初回起動時のみに限定する）。
///   4. 2回目以降の起動では signingkey.dat のみを読み、appsettings.json の
///      SigningKey は参照しない。
///
/// 重要: この鍵ペアは kbdaddid のものを絶対に再利用してはならない（新規に生成すること）。
/// kbdaddid の鍵を流用すると、kbdaddid が発行したライセンストークンが kbdlayer 側でも
/// 検証を通ってしまい（逆方向も同様）、2製品のライセンスが実質的に共有されてしまう。
/// 詳細は driver/kbdlayer/token.c のヘッダーコメントと driver/kbdlayer/KEYS.md を参照。
///
/// 残存する制約（正直に明記しておく）: DPAPI の LocalMachine スコープはマシンをまたいだ
/// 持ち出しを無意味にする（そのマシン固有の鍵で暗号化されるため）が、初回起動前の
/// インストーラー同梱物（appsettings.json/publish 出力）を分解すれば鍵材料自体は
/// 依然として取得可能である。これはローカルで署名を完結させるオフラインライセンス方式に
/// 本質的に伴う制約であり、DPAPI 化は「ソースやgit履歴に平文で残り続けること」と
/// 「インストール後も平文ファイルとしてディスクに残り続けること」を防ぐ効果に限定される。
/// </summary>
public sealed class TokenSigner : IDisposable
{
    private static readonly string ProtectedKeyPath = Path.Combine(ProtectedFolder.Dir, "signingkey.dat");

    private readonly ECDsa _ecdsa;

    public TokenSigner(IConfiguration config, ILogger<TokenSigner> logger)
    {
        ECParameters ecParams;

        byte[]? raw = TryReadProtectedKey(logger);
        if (raw is not null)
        {
            ecParams = DeserializeParameters(raw);
            logger.LogInformation("TokenSigner: loaded signing key from {Path}", ProtectedKeyPath);
        }
        else
        {
            logger.LogInformation("TokenSigner: no protected key found at {Path}; bootstrapping from configuration", ProtectedKeyPath);
            ecParams = ReadFromConfig(config);
            raw = SerializeParameters(ecParams);
            ProtectAndPersist(raw, logger);
            TryStripSigningKeyFromAppSettings(logger);
        }

        _ecdsa = ECDsa.Create(ecParams);
    }

    /// <param name="userId">Gumroad purchaser ID または email</param>
    /// <param name="machineGuid">HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</param>
    /// <param name="expUnix">有効期限 (Unix 秒)</param>
    public string CreateToken(string userId, string machineGuid, long expUnix)
    {
        var payload = new
        {
            uid = userId,
            mid = machineGuid,
            exp = expUnix,
            ver = 1
        };
        string payloadJson = JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = false });

        string payloadB64 = ToBase64Url(Encoding.UTF8.GetBytes(payloadJson));
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(payloadB64));
        byte[] sig = _ecdsa.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        string sigB64 = ToBase64Url(sig);
        return payloadB64 + "." + sigB64;
    }

    public void Dispose() => _ecdsa.Dispose();

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data)
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');

    // ---------------------------------------------------------------
    // DPAPI-protected key storage
    // ---------------------------------------------------------------

    private static byte[]? TryReadProtectedKey(ILogger logger)
    {
        if (!File.Exists(ProtectedKeyPath)) return null;
        try
        {
            byte[] protectedBytes = File.ReadAllBytes(ProtectedKeyPath);
            return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
        }
        catch (Exception ex)
        {
            // 壊れている/別マシンからコピーされた等で復号できない場合は再ブートストラップに
            // フォールバックする（appsettings.json の SigningKey がまだ残っていれば、の話。
            // 既にストリップ済みなら ReadFromConfig 側で分かりやすいエラーになる）。
            logger.LogWarning(ex, "Failed to unprotect signing key at {Path}; will attempt to re-bootstrap from configuration", ProtectedKeyPath);
            return null;
        }
    }

    private static void ProtectAndPersist(byte[] raw, ILogger logger)
    {
        byte[] protectedBytes = ProtectedData.Protect(raw, null, DataProtectionScope.LocalMachine);
        ProtectedFolder.Ensure();
        File.WriteAllBytes(ProtectedKeyPath, protectedBytes);
        logger.LogInformation("TokenSigner: signing key DPAPI-protected and persisted to {Path}", ProtectedKeyPath);
    }

    // Raw layout: X(32) || Y(32) || D(32), all big-endian — matches ECParameters.Q.X/Y/D sizes
    // for P-256 exactly, so no length-prefixing is needed.
    private static byte[] SerializeParameters(ECParameters p)
    {
        byte[] buf = new byte[96];
        Buffer.BlockCopy(p.Q.X!, 0, buf, 0, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, buf, 32, 32);
        Buffer.BlockCopy(p.D!, 0, buf, 64, 32);
        return buf;
    }

    private static ECParameters DeserializeParameters(byte[] raw)
    {
        if (raw.Length != 96)
            throw new InvalidOperationException($"Corrupt signing key material: expected 96 bytes, got {raw.Length}");

        byte[] x = new byte[32], y = new byte[32], d = new byte[32];
        Buffer.BlockCopy(raw, 0, x, 0, 32);
        Buffer.BlockCopy(raw, 32, y, 0, 32);
        Buffer.BlockCopy(raw, 64, d, 0, 32);

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
            D = d
        };
    }

    private static ECParameters ReadFromConfig(IConfiguration config)
    {
        string? xB64 = config["SigningKey:X"];
        string? yB64 = config["SigningKey:Y"];
        string? dB64 = config["SigningKey:D"];

        if (string.IsNullOrEmpty(xB64) || string.IsNullOrEmpty(yB64) || string.IsNullOrEmpty(dB64) ||
            xB64.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SigningKey:X/Y/D is not configured (or is still a placeholder) in appsettings.json, and no " +
                $"protected key was found at {ProtectedKeyPath}. Generate a fresh ECDSA P-256 key pair per " +
                "driver/kbdlayer/KEYS.md before first run -- do NOT reuse kbdaddid's key pair.");
        }

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Convert.FromBase64String(xB64), Y = Convert.FromBase64String(yB64) },
            D = Convert.FromBase64String(dB64)
        };
    }

    /// <summary>
    /// 初回ブートストラップ成功後、appsettings.json から SigningKey セクションを削除する
    /// （平文の秘密鍵がインストールフォルダに残り続ける期間を初回起動時のみに限定するため）。
    /// ベストエフォート: 失敗してもサービス動作には影響させない（signingkey.dat への
    /// 保存は既に完了しているため、次回起動以降はそちらが使われる）。
    /// </summary>
    private static void TryStripSigningKeyFromAppSettings(ILogger logger)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(path)) return;

            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            if (root is JsonObject obj && obj.ContainsKey("SigningKey"))
            {
                obj.Remove("SigningKey");
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                logger.LogInformation("TokenSigner: stripped SigningKey from {Path} after bootstrap", path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TokenSigner: failed to strip SigningKey from {Path} (non-fatal)", path);
        }
    }
}
