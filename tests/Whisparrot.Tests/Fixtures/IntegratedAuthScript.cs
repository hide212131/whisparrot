using System.Buffers.Binary;
using System.Text;

namespace Whisparrot.Tests.Fixtures;

/// <summary>プロキシが 407 で出すチャレンジの種類。</summary>
internal enum ChallengeMode { Ntlm, Negotiate, NtlmAndNegotiate, BasicOnly }

/// <summary>1 回の CONNECT 応答の判断。<see cref="Challenges"/> は <c>Proxy-Authenticate</c> ヘッダーの値。</summary>
internal sealed record AuthStep(bool Accept, IReadOnlyList<string> Challenges, bool Close = false);

/// <summary>
/// 統合認証（NTLM / Negotiate）の 407 チャレンジを返すスクリプト。メッセージの流れ（種別と、Type 3 の各欄の長さだけ）を <see cref="Events"/> に記録する。
/// 暗号の正しさは検証しない（Type 3 の中身は読まず、受け入れるかどうかは <paramref name="acceptType3"/> で決める）。
/// NTLM は接続に結び付くため、接続ごとに <see cref="NewConnection"/> で状態を分ける。
/// </summary>
internal sealed class IntegratedAuthScript(ChallengeMode mode, bool acceptType3 = true)
{
    private static readonly byte[] Sig = "NTLMSSP\0"u8.ToArray();
    private static readonly byte[] NtlmOid = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];
    private readonly object _lock = new();
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events { get { lock (_lock) return [.. _events]; } }

    private void Log(string e) { lock (_lock) _events.Add(e); }

    private IReadOnlyList<string> Initial() => mode switch
    {
        ChallengeMode.Ntlm => ["NTLM"],
        ChallengeMode.Negotiate => ["Negotiate"],
        ChallengeMode.NtlmAndNegotiate => ["Negotiate", "NTLM"],
        _ => ["Basic realm=\"test\""],
    };

    public Func<string?, AuthStep> NewConnection() => header =>
    {
        if (string.IsNullOrEmpty(header)) { Log("none"); return new AuthStep(false, Initial()); }
        var sp = header.IndexOf(' ');
        var scheme = sp < 0 ? header : header[..sp];
        var token = sp < 0 ? [] : TryBase64(header[(sp + 1)..]);
        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)) { Log("basic"); return new AuthStep(false, Initial(), true); }

        var isNegotiate = scheme.Equals("Negotiate", StringComparison.OrdinalIgnoreCase);
        var at = IndexOf(token, Sig);
        if (at < 0) { Log(isNegotiate ? "negotiate:no-ntlm-token" : "ntlm:malformed"); return new AuthStep(false, Initial(), true); }
        var ntlm = token.AsSpan(at);
        var type = ntlm.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(ntlm[8..]) : 0;
        var tag = isNegotiate ? "negotiate:" : "ntlm:";
        switch (type)
        {
            case 1:
                Log(tag + "type1" + (isNegotiate ? (at > 0 ? "[spnego]" : "[bare-ntlmssp]") : ""));
                var type2 = Type2();
                // Negotiate: クライアントが SPNEGO で包んで来たら包んで返し、素の NTLMSSP で来たら（IP ホスト宛てで Kerberos が使えない場合など）素のまま返す。
                var wrapped = isNegotiate && at > 0;
                var value = (isNegotiate ? "Negotiate " : "NTLM ") + Convert.ToBase64String(wrapped ? NegTokenResp(type2, accept: false) : type2);
                return new AuthStep(false, [value]);
            case 3:
                Log($"{tag}type3(user={Len(ntlm, 36)},domain={Len(ntlm, 28)},nt={Len(ntlm, 20)})");
                return acceptType3 ? new AuthStep(true, []) : new AuthStep(false, Initial(), true);
            default:
                Log(tag + "type" + type);
                return new AuthStep(false, Initial(), true);
        }
    };

    /// <summary>Type 3 の Security Buffer（長さ 2 バイト、最大長 2 バイト、オフセット 4 バイト）の長さ欄。</summary>
    private static int Len(ReadOnlySpan<byte> m, int fieldOffset) => m.Length >= fieldOffset + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(m[fieldOffset..]) : -1;

    private static byte[] TryBase64(string s)
    {
        try { return Convert.FromBase64String(s.Trim()); } catch (FormatException) { return []; }
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    /// <summary>書式だけ正しい Type 2（固定チャレンジ、NetBIOS 名と時刻の AV ペア）。</summary>
    private static byte[] Type2()
    {
        var target = Encoding.Unicode.GetBytes("WHISPARROT-TEST");
        var info = new List<byte>();
        void Av(ushort id, byte[] v) { info.AddRange(BitConverter.GetBytes(id)); info.AddRange(BitConverter.GetBytes((ushort)v.Length)); info.AddRange(v); }
        Av(2, target);
        Av(1, target);
        Av(7, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        Av(0, []);
        const int header = 56;
        var msg = new byte[header + target.Length + info.Count];
        Sig.CopyTo(msg, 0);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(12), (ushort)target.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(14), (ushort)target.Length);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(16), header);
        // Unicode | RequestTarget | NTLM | AlwaysSign | TargetTypeServer | ExtendedSessionSecurity | TargetInfo | Version | 128 | KeyExch | 56
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(20), 0xE2898215);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(msg, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(40), (ushort)info.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(42), (ushort)info.Count);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(44), header + target.Length);
        new byte[] { 10, 0, 0x63, 0x45, 0, 0, 0, 0x0F }.CopyTo(msg, 48);
        target.CopyTo(msg, header);
        info.CopyTo(msg, header + target.Length);
        return msg;
    }

    private static byte[] Der(byte tag, byte[] content)
    {
        var len = content.Length < 0x80 ? [(byte)content.Length]
            : content.Length <= 0xFF ? new byte[] { 0x81, (byte)content.Length }
            : [0x82, (byte)(content.Length >> 8), (byte)content.Length];
        return [tag, .. len, .. content];
    }

    /// <summary>SPNEGO の NegTokenResp（accept-incomplete、NTLMSSP、内側のトークン）。</summary>
    private static byte[] NegTokenResp(byte[] mechToken, bool accept)
    {
        byte[] state = Der(0xA0, [0x0A, 0x01, (byte)(accept ? 0 : 1)]);
        byte[] mech = Der(0xA1, NtlmOid);
        byte[] token = Der(0xA2, Der(0x04, mechToken));
        return Der(0xA1, Der(0x30, [.. state, .. mech, .. token]));
    }
}
