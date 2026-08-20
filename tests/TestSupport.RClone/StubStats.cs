using System.Text.Json.Nodes;

namespace TestSupport.RClone;

/// <summary>
/// The <c>core/stats</c> payload the stub returns, as plain settable numbers.
///
/// Deliberately not the agent's <c>JobStatsResult</c>: the stub emits field
/// names on the wire, and a test that drives the real client against it is
/// then a genuine two-sided check.
/// </summary>
public sealed class StubStats
{
    public long Bytes { get; set; }
    public long TotalBytes { get; set; }
    public int Checks { get; set; }
    public int TotalChecks { get; set; }
    public int Transfers { get; set; }
    public int TotalTransfers { get; set; }
    public int Errors { get; set; }
    public int Deletes { get; set; }
    public int Renames { get; set; }
    public int Listed { get; set; }
    public double Speed { get; set; }
    public double ElapsedTime { get; set; }
    public double TransferTime { get; set; }
    public double? Eta { get; set; }
    public bool FatalError { get; set; }
    public bool RetryError { get; set; }
    public string? LastError { get; set; }

    /// <summary>Entries reported under <c>transferring</c>.</summary>
    public List<StubTransferringItem> Transferring { get; } = new();

    /// <summary>File names reported under <c>checking</c>.</summary>
    public List<string> Checking { get; } = new();

    internal JsonObject ToJson()
    {
        var transferring = new JsonArray();
        foreach (var item in Transferring)
        {
            transferring.Add(item.ToJson());
        }

        var checking = new JsonArray();
        foreach (var name in Checking)
        {
            checking.Add(JsonValue.Create(name));
        }

        return new JsonObject
        {
            ["bytes"] = Bytes,
            ["totalBytes"] = TotalBytes,
            ["checks"] = Checks,
            ["totalChecks"] = TotalChecks,
            ["transfers"] = Transfers,
            ["totalTransfers"] = TotalTransfers,
            ["errors"] = Errors,
            ["deletes"] = Deletes,
            ["renames"] = Renames,
            ["listed"] = Listed,
            ["speed"] = Speed,
            ["elapsedTime"] = ElapsedTime,
            ["transferTime"] = TransferTime,
            ["eta"] = Eta is null ? null : JsonValue.Create(Eta.Value),
            ["fatalError"] = FatalError,
            ["retryError"] = RetryError,
            ["lastError"] = LastError is null ? null : JsonValue.Create(LastError),
            ["serverSideCopies"] = 0,
            ["serverSideCopyBytes"] = 0L,
            ["serverSideMoves"] = 0,
            ["serverSideMoveBytes"] = 0L,
            ["transferring"] = transferring,
            ["checking"] = checking
        };
    }
}

/// <summary>One entry of the <c>transferring</c> array.</summary>
public sealed class StubTransferringItem
{
    public string? Name { get; set; }
    public long Bytes { get; set; }
    public long Size { get; set; }
    public double Percentage { get; set; }
    public double Speed { get; set; }
    public double SpeedAvg { get; set; }
    public double? Eta { get; set; }
    public string? SrcFs { get; set; }
    public string? DstFs { get; set; }
    public string? Group { get; set; }

    internal JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["bytes"] = Bytes,
        ["size"] = Size,
        ["percentage"] = Percentage,
        ["speed"] = Speed,
        ["speedAvg"] = SpeedAvg,
        ["eta"] = Eta is null ? null : JsonValue.Create(Eta.Value),
        ["srcFs"] = SrcFs,
        ["dstFs"] = DstFs,
        ["group"] = Group
    };
}
