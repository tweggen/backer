using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        try
        {
            var result = await _http.GetFromJsonAsync<JobTransferStatsResult>("/jobtransfers");
            if (result != null)
            {
                _manager.UpdateOverallProgress(result.OverallStats);
                _manager.UpdateJobTransfers(result);
            }
        }
        catch
        {
            _manager.UpdateOverallProgress(null);
        }
    }

    /// <summary>
    /// Called by SignalR when job transfer stats are pushed from BackerAgent
    /// </summary>
    public void UpdateJobTransferStats(JobTransferStatsResult result)
    {
        _manager.UpdateOverallProgress(result.OverallStats);
        _manager.UpdateJobTransfers(result);
    }

    private async void OnAbortClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int jobId)
        {
            try
            {
                await _http.PostAsync($"/jobs/{jobId}/abort", null);
            }
            catch
            {
                // Best effort — job may have already finished
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
