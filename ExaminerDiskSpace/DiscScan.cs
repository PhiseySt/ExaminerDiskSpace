namespace ExaminerDiskSpace;

public class DiskScan : IDisposable
{
    private int _lastCommit = Environment.TickCount;
    private const int CommitFrequencyInMilliseconds = 2 /*minutes/commit*/ * 60000 /*seconds/minute*/;

    private object _workerExceptionLock = new();
    private readonly object _currentLock = new();
    private Exception? _workerException;

    /// <summary>UpdateInterval is the DataSize threshold at which we interrupt processing in order to 
    /// propagate Deltas up to the top-level so as to update the display.</summary>
    private static readonly DataSize UpdateInterval = DataSize.Gigabyte;

    public DiskScan(string? topPath)
    {
        _topPath = topPath;
        _worker = new Thread(WorkerThread);
        _worker.Start();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private bool _disposed;
    private bool _closing;
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // Free managed resources
            lock (_currentLock)
            {
                _closing = true;
            }

            if (_worker != null) { _worker.Join(); _worker = null; }
        }

        // Free unmanaged resources
        _disposed = true;
    }

    public void CheckHealth()
    {
        lock (_workerExceptionLock)
        {
            if (_workerException == null) return;
            var exc = _workerException;
            _workerException = null;
            throw exc;
        }
    }

    private Thread? _worker;
    private readonly string? _topPath;

    private DateTime _scanStartedUtc;

    /// <summary>
    /// ScanRoot contains the in-progress and final results of the scan.  Any given DirectorySummary should be lock'd 
    /// before reading.
    /// </summary>
    public DirectorySummary? ScanRoot;

    /// <summary>
    /// Apply lock to the DiskScan object before reading these values
    /// </summary>
    public long FilesScanned;
    public long FoldersScanned;

    public enum Activities
    {
        ScanningFolders,
        ScanningNewFolders,
        RescanningOldFolders,
        CommittingPartialResults,
        CommittingFinalResults,
        ScanComplete
    }
    public Activities CurrentActivity;          // For display/informational purposes.  Guarded by a lock on the DiskScan object.
    public bool IsScanComplete => CurrentActivity == Activities.ScanComplete;

    /** Processing Sequence:
 *                      
 *  1st Pass: Enumerate immediate subfolders.  Replace any entries in the enumeration that have previous
 *            versions with references to those previous versions.  Check for previous versions that are
 *            no longer present in the enumeration and reduce the parent's counters accordingly.  The
 *            folder being scanned can either be a previous result folder or a new folder, but when it is
 *            a new result folder it simply contains an empty Subfolders list and all subfolders are
 *            identified as "new additions".
 *  2nd Pass: Recurse into subfolders and perform 1st and 2nd pass within them.  When the 1st pass
 *            results in a size reduction in counters, apply that here and pass it up.  Sizes can decrease
 *            as we discover subfolders that are no longer present, but there should be no cause to increase 
 *            any sizes at this stage as we are not enumerating files yet.  Subfolder counts can increase.
 *  3rd Pass: Recurse into any subfolders that do not have a previous version.  Completely tabulate them
 *            and increase size on parent when completed.  Oldest and Newest stamps will be accurate for
 *            these leaves, but not up the tree.                     
 *  4th Pass: Recurse into any subfolders that do have a previous version.  Completely tabulate them
 *            and track the delta size.  Update parent with delta size.  Start this process at the bottoms
 *            of the tree so as the have minimal impact on the display until we have new results to show.
 *            By starting at the bottom of the tree and propagating upward, we can also propagate up
 *            new and accurate Oldest and Newest stamps.
 *
 */
    private void WorkerThread()
    {

        try
        {

            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            _scanStartedUtc = DateTime.UtcNow;

            lock (this) CurrentActivity = Activities.ScanningFolders;

            if (ScanResultFile.OpenFile != null)
                lock (ScanResultFile.OpenFile)
                {
                    _lastCommit = Environment.TickCount;

                    ScanRoot = ScanResultFile.OpenFile.Find(_topPath);
                    if (ScanRoot == null)
                    {
                        if (_topPath != null)
                        {
                            var diRoot = new DirectoryInfo(_topPath);
                            ScanRoot = new DirectorySummary(null, diRoot);
                        }
                    }

                    FirstPass(ScanRoot);
                    if (_closing) return;

                    SecondPass(ScanRoot);
                    if (_closing) return;

                    ScanResultFile.OpenFile.MergeResults(ScanRoot);
                    lock (this) CurrentActivity = Activities.ScanningNewFolders;

                    var completed = false;
                    while (!completed)
                    {
                        ThirdOrFourthPass(ScanRoot, false, out completed);
                        if (_closing) return;
                    }

                    lock (this) CurrentActivity = Activities.RescanningOldFolders;
                    completed = false;
                    while (!completed)
                    {
                        ThirdOrFourthPass(ScanRoot, true, out completed);
                        if (_closing) return;
                    }

                    lock (this) CurrentActivity = Activities.CommittingFinalResults;
                    ScanResultFile.Save();

                    lock (this) CurrentActivity = Activities.ScanComplete;
                }
        }
        catch (Exception exc)
        {
            lock (_workerExceptionLock) { _workerExceptionLock = exc; }
        }
    }

    /// <summary>
    /// Enumerate immediate subfolders.  Replace any entries in the enumeration that have previous
    /// versions with references to those previous versions.  Check for previous versions that are
    /// no longer present in the enumeration and reduce the parent's counters accordingly.  The
    /// folder being scanned can either be a previous result folder or a new folder, but when it is
    /// a new result folder it simply contains an empty Subfolders list and all subfolders are
    /// identified as "new additions".
    /// </summary>
    private DirectorySummary.DeltaCounters FirstPass(DirectorySummary? current)
    {
        var delta = new DirectorySummary.DeltaCounters();
        long localFoldersScanned = 0;

        var folders = Array.Empty<DirectoryInfo>();
        try
        {
            if (current is { FullName: { } })
            {
                var diInProgress = new DirectoryInfo(current.FullName);
                folders = diInProgress.GetDirectories();
            }
        }
        catch (Exception) // Skip/treat empty directories that we can't access.  They should show up as "unaccounted" or "outside".
        {
            folders = Array.Empty<DirectoryInfo>();
        }

        try

        {
            if (current != null)
                lock (current)
                {
                    // Current might be a DirectorySummary pulled from the ScanResultFile or it might be entirely new.
                    // We don't really need to distinguish here, as in the case of "entirely new" it will simply have
                    // an empty Subfolder list and everything will qualify as an "add".  

                    // First, look for subfolders that have been removed since the previous version.                    
                    for (var i = 0; i < current.Subfolders.Count;)
                    {
                        var ds = current.Subfolders[i];
                        if (_closing)
                        {
                            current.Adjust(delta);
                            return delta;
                        }

                        var wasRemoved = true;
                        foreach (var di in folders)
                        {
                            if (di.Name.Equals(ds?.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                wasRemoved = false;
                                break;
                            }
                        }

                        if (wasRemoved)
                        {
                            delta.Size -= ds.Size;
                            delta.TotalFiles -= ds.TotalFiles;
                            delta.TotalSubfolders -= ds.TotalSubfolders;
                            delta.TotalSubfolders--;
                            current.Subfolders.RemoveAt(i);
                        }
                        else i++;
                    }

                    // Now, look for new subfolders.

                    // This will involve a comparison for each file system folder against all folders in record.  That can be a painful
                    // operation.  To help, we first make a modifiable copy of the Current.Subfolders list and remove entries as they
                    // are found.  Sort of like checking them off a list, they won't be a burden on the next search through the list.
                    var previousList = new List<DirectorySummary?>(current.Subfolders);

                    foreach (var di in folders)
                    {
                        if (_closing)
                        {
                            current.Adjust(delta);
                            return delta;
                        }

                        // Check if there is a previous scan on this folder and use that as a starting point.
                        var foundPrevious = false;
                        for (var i = 0; i < previousList.Count;)
                        {
                            if (previousList[i]!.Name.Equals(di.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                previousList
                                    .RemoveAt(
                                        i); // Remove from the PreviousList, but it still exists in Current.Subfolders (the real listing).
                                foundPrevious = true;
                                break;
                            }

                            i++;
                        }

                        if (!foundPrevious)
                        {
                            try
                            {
                                // Interestingly, a System.IO.PathTooLongException can come up in the DirectorySummary
                                // constructor when it tries accessing di.Parent.  I could probably dig into the .NET libraries
                                // and why they allow it to end up this way and whether there is a better solution, but for now 
                                // we'll just discard these cases.
                                current.Subfolders.Add(new DirectorySummary(current, di));
                                delta.TotalSubfolders++;
                            }
                            catch (PathTooLongException)
                            {
                            } // Skip/treat empty directories that we can't access.  They should show up as "unaccounted" or "outside".                            
                        }

                        localFoldersScanned++;
                    }

                    // Apply the accumulated counter changes to Current.  We will still need to propagate these deltas up, as well.
                    // This step also invalidates any Oldest/Newest timestamps, which will need to be reconsidered from the bottom up.
                    current.Adjust(delta);
                }
        }
        catch (Exception exc)
        {
            throw new Exception(exc.Message + " (while examining directories)", exc);
        }


        lock (this) { FoldersScanned += localFoldersScanned; }
        return delta;
    }


    /// <summary>
    /// Recurse into subfolders and perform 1st and 2nd pass within them.  When the 1st pass
    /// results in a size reduction in counters, apply that here and pass it up.  Sizes can decrease
    /// as we discover subfolders that are no longer present, but there should be no cause to increase 
    /// any sizes at this stage as we are not enumerating files yet.  Subfolder counts can increase.
    /// </summary>
    private DirectorySummary.DeltaCounters SecondPass(DirectorySummary? current)
    {
        var delta = new DirectorySummary.DeltaCounters();

        if (current != null)
        {
            foreach (var dsFolder in current.Subfolders)
            {
                delta += FirstPass(dsFolder);
                if (_closing)
                {
                    lock (current) current.Adjust(delta);
                    return delta;
                }
            }

            foreach (var dsFolder in current.Subfolders)
            {
                delta += SecondPass(dsFolder);
                if (_closing)
                {
                    lock (current) current.Adjust(delta);
                    return delta;
                }
            }

            lock (current)
            {
                current.Adjust(delta);
            }
        }

        return delta;
    }

    /// <summary>
    /// Recurse into any subfolders that do not have a previous version.  Completely tabulate them
    /// and increase size on parent when completed.  Oldest and Newest stamps will be accurate for
    /// these leaves, but not up the tree.
    /// </summary>        
    private DirectorySummary.DeltaCounters ThirdOrFourthPass(DirectorySummary? current, bool fourthPass, out bool completed)
    {
        var criteria = fourthPass ? _scanStartedUtc : DateTime.MinValue;

        var absolute = new DirectorySummary.DeltaCounters();
        var delta = new DirectorySummary.DeltaCounters();
        var newest = DateTime.MinValue;
        var oldest = DateTime.MaxValue;

        if (current != null)
        {
            foreach (var dsFolder in current.Subfolders)
            {
                // Check whether we've completed this one (3rd or 4th pass), or if this one has previous version information (3rd pass).
                if (dsFolder != null && dsFolder.LastScanUtc <= criteria)
                {
                    delta += ThirdOrFourthPass(dsFolder, fourthPass, out var subCompleted);
                    if (_closing)
                    {
                        lock (current) current.Adjust(delta);
                        completed = false;
                        return delta;
                    }

                    if (!subCompleted || delta.Size >= UpdateInterval)
                    {
                        lock (current) current.Adjust(delta);
                        completed = false;
                        return delta;
                    }
                }

                // dsFolder has now completed its third/fourth pass and can be counted.  This may have been from previous scans or a previous call to ThirdOrFourthPass,
                // or it may have completed just now, but it's complete.

                if (dsFolder != null)
                {
                    absolute.Size += dsFolder.Size;
                    absolute.TotalFiles += dsFolder.TotalFiles;
                    absolute.TotalSubfolders += dsFolder.TotalSubfolders;
                    absolute.TotalSubfolders++;
                    if (dsFolder.Oldest < oldest) oldest = dsFolder.Oldest;
                    if (dsFolder.Newest > newest) newest = dsFolder.Newest;
                }
            }

            var files = Array.Empty<FileInfo>();
            try
            {
                if (current.FullName != null)
                {
                    var diInProgress = new DirectoryInfo(current.FullName);
                    files = diInProgress.GetFiles();
                }
            }
            catch (Exception)
            {
                files = Array.Empty<FileInfo>();
            } // Skip/treat empty directories that we can't access.  They should show up as "unaccounted" or "outside".

            // We don't retain any information on individual files, so we cannot compute a Delta as we go.  However, we know the size of all of our subfolders (we've been
            // counting in Absolute) and we are about the count the size of the files as they stand on disk.  From this, we can compute a final delta.

            var addnFilesScanned = absolute.TotalFiles;
            var sinceUpdate = Environment.TickCount;
            try
            {
                foreach (var fi in files)
                {
                    if (_closing)
                    {
                        lock (current)
                        {
                            current.Adjust(delta);
                            completed = false;
                            return delta;
                        }
                    }

                    if (fi.LastWriteTimeUtc < oldest) oldest = fi.LastWriteTimeUtc;
                    if (fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
                    absolute.Size += FileUtility.GetFileSizeOnDisk(fi);
                    absolute.TotalFiles++;
                    addnFilesScanned++;

                    if (Environment.TickCount - sinceUpdate > 5000)
                    {
                        lock (this)
                        {
                            FilesScanned += addnFilesScanned;
                            addnFilesScanned = 0;
                        }

                        sinceUpdate = Environment.TickCount;
                    }
                }
            }
            catch (Exception)
            {
            }

            delta.Size = absolute.Size - current.Size;
            delta.TotalFiles = absolute.TotalFiles - current.TotalFiles;
            delta.TotalSubfolders = absolute.TotalSubfolders - current.TotalSubfolders;

            current.Size = absolute.Size;
            current.TotalFiles = absolute.TotalFiles;
            current.TotalSubfolders = absolute.TotalSubfolders;
            current.Oldest = oldest;
            current.Newest = newest;
            current.LastScanUtc = DateTime.UtcNow;
            completed = true;

            // The choice of when to CullDetails is tricky.  If we do it while updating a folder (i.e. the recursion loop above) then we run the risk of modifying
            // the content of Subfolders only to have to re-add the information after an UpdateInterval comes back around.  That could lead to a cycle where we
            // spend all our time culling, committing, and re-examining.  Doing it here fails to cull things out of higher level folders, but that's desirable because
            // we are only talking about culling the in-memory representation and we still need those higher levels (they haven't completed yet).  The XML serialization
            // will automatically cull any small directories before storing to the ScanResultFile, so those higher levels will be culled out as far as disk storage
            // is concerned.
            current.CullDetails();

            // Committing the ScanResultFile is both infrequent and time consuming.  Our results in-memory are valid, that's the best state to quit in.  Not committing
            // to disk is a bit painful, but if they're closing, let's be responsive.  The previous commit should be there anyway.
            if (_closing)
            {
                return delta;
            }

            var elapsed = Environment.TickCount - _lastCommit;
            if (elapsed > CommitFrequencyInMilliseconds)
            {
                Activities wasDoing;
                lock (this)
                {
                    wasDoing = CurrentActivity;
                    CurrentActivity = Activities.CommittingPartialResults;
                }

                ScanResultFile.Save();
                lock (this)
                {
                    CurrentActivity = wasDoing;
                }

                _lastCommit = Environment.TickCount;
            }

            lock (this)
            {
                FilesScanned += addnFilesScanned;
            }
        }

        completed = true;
        return delta;
    }
}