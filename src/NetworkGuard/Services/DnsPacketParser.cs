using System.Text;

namespace NetworkGuard.Services;

public static class DnsPacketParser
{
    public static DnsQueryInfo? TryParse(byte[] packet, int length)
    {
        if (length < 12) return null;

        var transactionId = (ushort)((packet[0] << 8) | packet[1]);
        var questionCount = (ushort)((packet[4] << 8) | packet[5]);
        if (questionCount == 0) return null;

        // Parse domain name starting at offset 12 (after the 12-byte header)
        var offset = 12;
        var labels = new List<string>();

        while (offset < length)
        {
            var labelLength = packet[offset];
            if (labelLength == 0)
            {
                offset++;
                break;
            }

            // Check for compression pointer (0xC0) — shouldn't appear in queries but handle gracefully
            if ((labelLength & 0xC0) == 0xC0)
                return null;

            if (offset + 1 + labelLength > length) return null;

            labels.Add(Encoding.ASCII.GetString(packet, offset + 1, labelLength));
            offset += 1 + labelLength;
        }

        if (labels.Count == 0 || offset + 4 > length) return null;

        var queryType = (ushort)((packet[offset] << 8) | packet[offset + 1]);

        return new DnsQueryInfo
        {
            TransactionId = transactionId,
            Domain = string.Join('.', labels).ToLowerInvariant(),
            QueryType = QueryTypeToString(queryType)
        };
    }

    private static string QueryTypeToString(ushort type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        65 => "HTTPS",
        _ => $"TYPE{type}"
    };
}

public class DnsQueryInfo
{
    public ushort TransactionId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string QueryType { get; set; } = string.Empty;
}
