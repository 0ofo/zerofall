using System;
using System.Collections.Generic;
using System.Text;

namespace ZeroFall.AssetRecon.Services;

/// <summary>
/// Goby / Nmap 类「UDP 指纹」思路：<b>按端口选探测包</b>（RFC 或业界常见形态），在超时内收到 UDP 回包即认为「有响应」；
/// 再靠 Banner 文本/十六进制摘要人工或后续加「响应匹配规则」逼近 Goby 的指纹库（本表可自由扩展）。
/// </summary>
public static class UdpServiceProbes
{
    /// <param name="RuleName">规则名，写入 <see cref="Models.PortScanRow.Fingerprint"/>，便于与自建 YAML/JSON 规则库对齐。</param>
    public readonly record struct UdpProbe(string RuleName, ReadOnlyMemory<byte> Payload);

    private static readonly byte[] DnsRootAQuery =
    [
        0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x01
    ];

    /// <summary>SNMPv1 GetRequest community public, OID 1.3.6.1.2.1.1.1.0 (sysDescr.0)，BER 形态与 snmpget 一致。</summary>
    private static readonly byte[] SnmpV1GetSysDescr0 = Convert.FromHexString(
        "302902010004067075626C6963A01C020400000000020100020100300E300C06082B060102010101000500");

    private static readonly byte[] NtpClientV4 = CreateNtpV4();

    private static readonly byte[] StunBindingRequest =
    [
        0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xa4, 0x42,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc
    ];

    private static readonly byte[] MsSqlBrowserPing = [0x02];

    private static readonly byte[] RipV2Request =
    [
        0x01, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10
    ];

    private static readonly byte[] TftpReadRequest =
    [
        0x00, 0x01, 0x61, 0x00, 0x6f, 0x63, 0x74, 0x65, 0x74, 0x74, 0x00
    ]; // RRQ "a", mode "octet"

    private static readonly byte[] MemcachedVersion = "version\r\n"u8.ToArray();

    private static readonly byte[] SourceEngineQuery =
    [
        0xff, 0xff, 0xff, 0xff, 0x54, 0x53, 0x6f, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6e, 0x67, 0x69,
        0x6e, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00
    ];

    private static readonly byte[] NetBiosNameQueryWildcard = CreateNbnsWildcardQuery();

    private static readonly byte[] SsdpMSearch = Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "ST: ssdp:all\r\n" +
        "\r\n");

    private static readonly byte[] SipOptions = Encoding.ASCII.GetBytes(
        "OPTIONS sip:x@y SIP/2.0\r\n" +
        "Via: SIP/2.0/UDP 0.0.0.0\r\n" +
        "From: <sip:a@b>;tag=1\r\n" +
        "To: <sip:a@b>\r\n" +
        "Call-ID: df-udp-1\r\n" +
        "CSeq: 1 OPTIONS\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n");

    private static readonly byte[] SyslogPing = "<0>zerofall\r\n"u8.ToArray();

    /// <summary>未知端口：依次尝试若干通用/弱协议探测（仍远少于完整 Nmap/Goby 库）。</summary>
    private static readonly UdpProbe[] DefaultProbeSequence =
    [
        new("null", new byte[] { 0x00 }),
        new("dns-root-A", DnsRootAQuery),
        new("stun-bind", StunBindingRequest),
    ];

    private static readonly Dictionary<int, UdpProbe[]> PortSpecific = new()
    {
        [53] = [new UdpProbe("dns-root-A", DnsRootAQuery), new UdpProbe("null", new byte[] { 0x00 })],
        [5353] = [new UdpProbe("mdns-dns", DnsRootAQuery), new UdpProbe("null", new byte[] { 0x00 })],
        [69] = [new UdpProbe("tftp-rrq", TftpReadRequest)],
        [123] = [new UdpProbe("ntp-client", NtpClientV4)],
        [161] = [new UdpProbe("snmp-sysDescr", SnmpV1GetSysDescr0), new UdpProbe("null", new byte[] { 0x00 })],
        [162] = [new UdpProbe("snmp-sysDescr", SnmpV1GetSysDescr0), new UdpProbe("null", new byte[] { 0x00 })],
        [137] = [new UdpProbe("netbios-ns", NetBiosNameQueryWildcard), new UdpProbe("null", new byte[] { 0x00 })],
        [1900] = [new UdpProbe("ssdp-msearch", SsdpMSearch)],
        [500] = [new UdpProbe("ike-pad4", new byte[] { 0x00, 0x00, 0x00, 0x00 })],
        [514] = [new UdpProbe("syslog-line", SyslogPing)],
        [520] = [new UdpProbe("rip-req", RipV2Request)],
        [1434] = [new UdpProbe("mssql-ping", MsSqlBrowserPing)],
        [3478] = [new UdpProbe("stun-bind", StunBindingRequest), new UdpProbe("null", new byte[] { 0x00 })],
        [5060] = [new UdpProbe("sip-options", SipOptions)],
        [11211] = [new UdpProbe("memcached-version", MemcachedVersion)],
        [27015] = [new UdpProbe("source-query", SourceEngineQuery)],
    };

    private static byte[] CreateNtpV4()
    {
        var b = new byte[48];
        b[0] = 0x1b; // LI=0, VN=4, Mode=3 (client)
        return b;
    }

    /// <summary>NBNS 对「*」的编码形态（32 字节名段 + 固定头），与常见扫描器一致。</summary>
    private static byte[] CreateNbnsWildcardQuery()
    {
        var s = new byte[50];
        s[0] = 0x80;
        s[1] = 0xf0;
        s[2] = 0x00;
        s[3] = 0x10;
        s[4] = 0x00;
        s[5] = 0x01;
        s[6] = s[7] = s[8] = s[9] = s[10] = s[11] = 0;
        s[12] = 0x20;
        "CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8.CopyTo(s.AsSpan(13));
        s[45] = 0x00;
        s[46] = 0x00;
        s[47] = 0x21;
        s[48] = 0x00;
        s[49] = 0x01;
        return s;
    }

    /// <summary>返回对该端口依次发送的探测序列（命中即停）。</summary>
    public static UdpProbe[] GetProbeSequence(int port) =>
        PortSpecific.TryGetValue(port, out var seq) ? seq : DefaultProbeSequence;
}
