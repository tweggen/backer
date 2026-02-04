using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkerRClone.Models;

namespace YourBacker;

/// <summary>
/// Manages the collection of active file transfers and overall progress
/// </summary>
public partial class TransferManager : ObservableObject
{
    public ObservableCollection<FileTransferViewModel> Transfers { get; } = new();

    /// <summary>
    /// Overall transfer progress statistics
    /// </summary>
    [ObservableProperty]
    private OverallProgressViewModel _overallProgress = new();

    /// <summary>
    /// True when there are no active transfers
    /// </summary>
    public bool IsEmpty => Transfers.Count == 0 && !OverallProgress.HasActiveTransfers;

    public TransferManager()
    {
        // Update IsEmpty when collection changes
        Transfers.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Update overall progress from aggregate stats
    /// </summary>
    public void UpdateOverallProgress(OverallTransferStats? stats)
    {
        if (stats == null)
        {
            OverallProgress.HasActiveTransfers = false;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        OverallProgress.ProgressPercent = stats.ProgressPercent;
        OverallProgress.BytesTransferred = stats.BytesTransferred;
        OverallProgress.TotalBytes = stats.TotalBytes;
        OverallProgress.Speed = stats.Speed;
        OverallProgress.EtaSeconds = stats.EtaSeconds;
        OverallProgress.FilesCompleted = stats.FilesCompleted;
        OverallProgress.TotalFiles = stats.TotalFiles;
        OverallProgress.Errors = stats.Errors;
        OverallProgress.HasActiveTransfers = stats.HasActiveTransfers;

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Called periodically with fresh stats from the service
    /// </summary>
    public void UpdateTransfers(IEnumerable<FileTransferStats> stats)
    {
        var now = DateTime.UtcNow;

        // 1. Update or add items
        foreach (var stat in stats)
        {
            Debug.WriteLine($"TransferManager: Updating transfer {stat.Id} with state {stat.State}");
            
            var existing = Transfers.FirstOrDefault(t => t.Id == stat.Id);
            if (existing != null)
            {
                // Update properties
                existing.Progress = stat.Progress;
                existing.Speed = stat.Speed;
                existing.State = stat.State;
                existing.Size = stat.Size;
                existing.SourcePath = stat.SourcePath;
                existing.DestinationPath = stat.DestinationPath;
                existing.LastUpdated = now;
            }
            else
            {
                // Add new item
                Transfers.Add(new FileTransferViewModel
                {
                    Id = stat.Id,
                    Progress = stat.Progress,
                    Speed = stat.Speed,
                    State = stat.State,
                    Size = stat.Size,
                    SourcePath = stat.SourcePath,
                    DestinationPath = stat.DestinationPath,
                    LastUpdated = now
                });
            }
        }

        // 2. Mark items not in current stats as done
        for (int i = 0; i < Transfers.Count; ++i)
        {
            var item = Transfers[i];
            if (now != item.LastUpdated && item.State == "transferring")
            {
                var newItem = new FileTransferViewModel(item)
                {
                    Progress = 100f,
                    State = "done"
                };
                Transfers[i] = newItem;
            }
        }

        // 3. Remove completed items after 10 seconds
        var toRemove = Transfers
            .Where(item => item.State == "done" && (now - item.LastUpdated).TotalSeconds > 10)
            .ToList();
        
        foreach (var item in toRemove)
        {
            Transfers.Remove(item);
        }
    }
}
