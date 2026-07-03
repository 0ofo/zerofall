namespace ZeroFall.AssetRecon.Models;

/// <summary>单条扫描结果：TCP 为已建立连接；UDP 为在超时内收到目标回包。</summary>
public sealed class PortScanRow
{
    /// <summary>TCP 或 UDP。</summary>
    public string Protocol { get; set; } = "TCP";

    /// <summary>
    /// UDP：触达回包的探测规则名（见 <c>UdpServiceProbes</c>），用于对齐 Goby 式「规则 id」；TCP 可留空或后续填 Banner 规则名。
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public int Port { get; set; }
    /// <summary>TCP：连接耗时；UDP：发起到首包耗时。</summary>
    public int ConnectMs { get; set; }
    public string Banner { get; set; } = string.Empty;
}
