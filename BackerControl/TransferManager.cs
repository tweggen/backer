using System.Collections.ObjectModel;
using System.Diagnostics;

namespace BackerControl;


public class TransferManager
{
    public ObservableCollection<FileTransferViewModel> Transfers { get; }
        = new ObservableCollection<FileTransferViewModel>();

    // Called once per second with fresh stats
    public void UpdateTransfers(IEnumerable<FileTransferStats> stats)
    {
        var now = DateTime.UtcNow;

        /*
         * 1. Update or add items
         */
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
        
        /*
         * 2. Everything that is not covered by the transfer stats is not
         * transferring any more, set it to done.
         * TXWTODO: It would have to be in errors otherwise.
         */
        for (int i = 0; i < Transfers.Count; ++i)
        {
            var item = Transfers[i];
            if (now != item.LastUpdated && item.State == "transferring")
            {
                FileTransferViewModel newItem = new FileTransferViewModel(item);
                newItem.Progress = 100f;
                newItem.State = "done";
                Transfers[i] = newItem;
            }
        }

        /*
         * 3. Schedule removal of completed items
         */
        foreach (var item in Transfers.ToList())
        {
            if (item.State == "done" &&
                (now - item.LastUpdated).TotalSeconds > 10)
            {
                Transfers.Remove(item);
            }
        }
    }
}