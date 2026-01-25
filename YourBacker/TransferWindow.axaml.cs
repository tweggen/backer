using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using WorkerRClone.Models;

namespace YourBacker;

public partial class TransferWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TransferManager _manager;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5931") };

    public TransferWindow()
    {
        InitializeComponent();

        _manager = new TransferManager();
        DataContext = _manager;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        var listStats = new List<FileTransferStats>();
        
        try
        {
            var transferStatsResult = await _http.GetFromJsonAsync<TransferStatsResult>("/transfers");
            
            if (transferStatsResult?.TransferringItems != null)
            {
                foreach (var item in transferStatsResult.TransferringItems)
                {
                    var fts = new FileTransferStats
                    {
                        Id = item.Name,
                        Speed = item.Speed,
                        Progress = item.PercentDone,
                        State = "transferring",
                        Size = item.TotalSize,
                        DestinationPath = item.Name,
                        SourcePath = item.Name
                    };
                    listStats.Insert(0, fts);
                }
            }
        }
        catch
        {
            // Unable to get stats from service - might be disconnected
            // That's ok, SignalR will push updates when available
        }

        _manager.UpdateTransfers(listStats);
    }

    /// <summary>
    /// Called by SignalR when transfer stats are pushed from BackerAgent
    /// </summary>
    public void UpdateTransferStats(TransferStatsResult transferStatsResult)
    {
        var listStats = new List<FileTransferStats>();
        
        if (transferStatsResult?.TransferringItems != null)
        {
            foreach (var item in transferStatsResult.TransferringItems)
            {
                var fts = new FileTransferStats
                {
                    Id = item.Name,
                    Speed = item.Speed,
                    Progress = item.PercentDone,
                    State = "transferring",
                    Size = item.TotalSize,
                    DestinationPath = item.Name,
                    SourcePath = item.Name
                };
                listStats.Insert(0, fts);
            }
        }

        _manager.UpdateTransfers(listStats);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
