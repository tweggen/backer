using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkerRClone.Models;

namespace YourBacker;

/// <summary>
/// Manages the collection of active jobs and their transfers, plus overall progress
/// </summary>
public partial class TransferManager : ObservableObject
{
    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    [ObservableProperty]
    private OverallProgressViewModel _overallProgress = new();

    public bool IsEmpty => Jobs.Count == 0 && !OverallProgress.HasActiveTransfers;

    public TransferManager()
    {
        Jobs.CollectionChanged += (s, e) => OnPropertyChanged(nameof(IsEmpty));
    }

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

    public void UpdateJobTransfers(JobTransferStatsResult result)
    {
        var now = DateTime.UtcNow;
        var seenIds = new HashSet<int>();

        foreach (var jobInfo in result.Jobs)
        {
            seenIds.Add(jobInfo.HannibalJobId);

            var jobVm = Jobs.FirstOrDefault(j => j.HannibalJobId == jobInfo.HannibalJobId);
            if (jobVm == null)
            {
                jobVm = new JobViewModel { HannibalJobId = jobInfo.HannibalJobId };
                Jobs.Add(jobVm);
            }

            jobVm.Tag = jobInfo.Tag;
            jobVm.SourcePath = jobInfo.SourcePath;
            jobVm.DestinationPath = jobInfo.DestinationPath;
            jobVm.StartedAt = jobInfo.StartedAt;
            jobVm.LastTransferActivity = jobInfo.LastTransferActivity;
            jobVm.ErrorCount = jobInfo.ErrorCount;

            // Compute status text
            if (jobInfo.Transfers.Count == 0)
            {
                if (jobInfo.LastTransferActivity == null)
                {
                    jobVm.StatusText = $"Started at {jobInfo.StartedAt.ToLocalTime():HH:mm}, preparing...";
                }
                else
                {
                    var idle = now - jobInfo.LastTransferActivity.Value;
                    jobVm.StatusText = $"No transfer for {FormatDuration(idle)}";
                }
            }
            else
            {
                jobVm.StatusText = "";
            }

            // Update per-job progress
            if (jobInfo.Stats != null)
            {
                jobVm.Progress.ProgressPercent = jobInfo.Stats.ProgressPercent;
                jobVm.Progress.BytesTransferred = jobInfo.Stats.BytesTransferred;
                jobVm.Progress.TotalBytes = jobInfo.Stats.TotalBytes;
                jobVm.Progress.Speed = jobInfo.Stats.Speed;
                jobVm.Progress.EtaSeconds = jobInfo.Stats.EtaSeconds;
                jobVm.Progress.FilesCompleted = jobInfo.Stats.FilesCompleted;
                jobVm.Progress.TotalFiles = jobInfo.Stats.TotalFiles;
                jobVm.Progress.Errors = jobInfo.Stats.Errors;
                jobVm.Progress.HasActiveTransfers = jobInfo.Stats.HasActiveTransfers;
            }

            // Update transfers within job
            UpdateTransfersForJob(jobVm, jobInfo.Transfers, now);

            // Update errors
            jobVm.Errors.Clear();
            foreach (var err in jobInfo.Errors)
            {
                jobVm.Errors.Add(err);
            }
        }

        // Remove jobs no longer present
        for (int i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!seenIds.Contains(Jobs[i].HannibalJobId))
            {
                Jobs.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdateTransfersForJob(JobViewModel jobVm, List<ItemTransferStatus> transfers, DateTime now)
    {
        var seenNames = new HashSet<string>();

        foreach (var item in transfers)
        {
            seenNames.Add(item.Name);
            var existing = jobVm.Transfers.FirstOrDefault(t => t.Id == item.Name);
            if (existing != null)
            {
                existing.Progress = item.PercentDone;
                existing.Speed = item.Speed;
                existing.State = "transferring";
                existing.Size = item.TotalSize;
                existing.SourcePath = item.Name;
                existing.DestinationPath = item.Name;
                existing.LastUpdated = now;
            }
            else
            {
                jobVm.Transfers.Add(new FileTransferViewModel
                {
                    Id = item.Name,
                    Progress = item.PercentDone,
                    Speed = item.Speed,
                    State = "transferring",
                    Size = item.TotalSize,
                    SourcePath = item.Name,
                    DestinationPath = item.Name,
                    LastUpdated = now
                });
            }
        }

        // Mark missing transfers as done, remove old done items
        for (int i = jobVm.Transfers.Count - 1; i >= 0; i--)
        {
            var t = jobVm.Transfers[i];
            if (!seenNames.Contains(t.Id))
            {
                if (t.State == "transferring")
                {
                    jobVm.Transfers[i] = new FileTransferViewModel(t) { Progress = 100f, State = "done" };
                }
                else if (t.State == "done" && (now - t.LastUpdated).TotalSeconds > 10)
                {
                    jobVm.Transfers.RemoveAt(i);
                }
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m";
        return $"{(int)duration.TotalSeconds}s";
    }
}
