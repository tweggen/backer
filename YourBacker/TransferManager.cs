using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YourBacker;

/// <summary>
/// Manages the collection of active file transfers
/// </summary>
public partial class TransferManager : ObservableObject
{
    public ObservableCollection<FileTransferViewModel> Transfers { get; } = new();

    /// <summary>
    /// True when there are no active transfers
    /// </summary>
    public bool IsEmpty => Transfers.Count == 0;

    public TransferManager()
    {
        // Update IsEmpty when collection changes
        Transfers.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsEmpty));
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
