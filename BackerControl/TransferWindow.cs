using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace BackerControl;

public partial class TransferWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TransferManager _manager;

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

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Simulate reading fresh stats from your service
        var stats = FileTransferService.ReadCurrentStats();

        // Update the observable collection
        _manager.UpdateTransfers(stats);
    }
}
