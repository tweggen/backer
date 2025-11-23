using System.Collections.ObjectModel;

namespace BackerControl;


public class TransferManager
{
    public ObservableCollection<FileTransferViewModel> Transfers { get; }
        = new ObservableCollection<FileTransferViewModel>();

    // Called once per second with fresh stats
    public void UpdateTransfers(IEnumerable<FileTransferStats> stats)
    {
        var now = DateTime.UtcNow;

        // 1. Update or add items
        foreach (var stat in stats)
        {
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
                    SourcePath = stat.SourcePath,
                    DestinationPath = stat.DestinationPath,
                    Progress = stat.Progress,
                    Speed = stat.Speed,
                    Size = stat.Size,
                    State = stat.State,
                    LastUpdated = now
                });
            }
        }

        // 2. Schedule removal of completed items
        foreach (var item in Transfers.ToList())
        {
            if (item.State == "Completed" &&
                (now - item.LastUpdated).TotalSeconds > 10)
            {
                Transfers.Remove(item);
            }
        }
    }
}