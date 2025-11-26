namespace WorkerRClone.Client.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public class JobStatsResult
{
    [JsonPropertyName("bytes")]
    public long bytes { get; set; }

    [JsonPropertyName("checks")]
    public int checks { get; set; }

    [JsonPropertyName("deletes")]
    public int deletes { get; set; }

    [JsonPropertyName("elapsedTime")]
    public double elapsedTime { get; set; }

    [JsonPropertyName("errors")]
    public int errors { get; set; }

    [JsonPropertyName("eta")]
    public double eta { get; set; }

    [JsonPropertyName("fatalError")]
    public bool fatalError { get; set; }

    [JsonPropertyName("lastError")]
    public string lastError { get; set; }

    [JsonPropertyName("renames")]
    public int renames { get; set; }

    [JsonPropertyName("listed")]
    public int listed { get; set; }

    [JsonPropertyName("retryError")] public bool retryError { get; set; }

    [JsonPropertyName("serverSideCopies")] public int serverSideCopies { get; set; }

    [JsonPropertyName("serverSideCopyBytes")]
    public long serverSideCopyBytes { get; set; }

    [JsonPropertyName("serverSideMoves")] public int serverSideMoves { get; set; }

    [JsonPropertyName("serverSideMoveBytes")]
    public long serverSideMoveBytes { get; set; }

    [JsonPropertyName("speed")] public double speed { get; set; }

    [JsonPropertyName("totalBytes")] public long totalBytes { get; set; }

    [JsonPropertyName("totalChecks")] public int totalChecks { get; set; }

    [JsonPropertyName("totalTransfers")] public int totalTransfers { get; set; }

    [JsonPropertyName("transferTime")] public double transferTime { get; set; }

    [JsonPropertyName("transfers")] public int transfers { get; set; }

    [JsonPropertyName("transferring")] public List<TransferringItem> transferring { get; set; }

    [JsonPropertyName("checking")] public List<string> checking { get; set; }
}

public class TransferringItem
{
    [JsonPropertyName("bytes")] public long bytes { get; set; }

    [JsonPropertyName("eta")] public double eta { get; set; }

    [JsonPropertyName("name")] public string name { get; set; }

    [JsonPropertyName("percentage")] public double percentage { get; set; }

    [JsonPropertyName("speed")]
    public double speed { get; set; }

    [JsonPropertyName("speedAvg")]
    public double speedAvg { get; set; }

    [JsonPropertyName("size")]
    public long size { get; set; }
}
