using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using WorkerRClone.Configuration;
using WorkerRClone.Models;

namespace BackerControl;

public partial class TransferWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TransferManager _manager;

    private HttpClient http = new() { BaseAddress = new Uri("http://localhost:5931") };
    
    public TransferWindow()
    {
        InitializeComponent();

        _manager = new TransferManager();
        DataContext = _manager; // bind Transfers to ItemsControl

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private bool once = true;

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (once)
        {
            once = false;
            // Simulate reading fresh stats from your service
            var transferStatsResult = await http.GetFromJsonAsync<TransferStatsResult>("/transfers");
            List<FileTransferStats> listStats = new();
            foreach (var item in transferStatsResult.TransferringItems)
            {
                FileTransferStats fts = new()
                {
                    Id = item.Name,
                    Speed = item.Speed,
                    Progress = item.PercentDone,
                    State = "transferring...",
                    Size = item.TotalSize,
                    DestinationPath = "",
                    SourcePath = ""
                };
                listStats.Add(fts);
            }

            /*
             * Update the observable collection
             */
            _manager.UpdateTransfers(listStats);
        }
    }
}