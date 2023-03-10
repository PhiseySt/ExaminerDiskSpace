using System.Text;
using System.Windows.Forms.DataVisualization.Charting;

namespace ExaminerDiskSpace
{

    public partial class MainForm : Form
    {
        private DiskScan _currentScan;

        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Resize(object? sender, EventArgs? e)
        {
            btnSelectFolder.Width = ClientSize.Width - 2 * btnSelectFolder.Left;

            lblFolderName.Width = btnSelectFolder.Width;
            lblFolderName.Left = btnSelectFolder.Left;

            PieChart.Width = ClientSize.Width - 2 * PieChart.Left;
            PieChart.Height = ClientSize.Height - PieChart.Top - btnSelectFolder.Top - 3 * lblScanStatus.Height - lblInstructions.Height - btnSelectFolder.Top / 2;

            lblScanStatus.Top = PieChart.Bottom + lblScanStatus.Height;
            lblInstructions.Top = ClientSize.Height - lblInstructions.Height - lblScanStatus.Height;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            MainForm_Resize(null, null);

            PieChart.Series[0].Points.Clear();

            GUITimer_Tick(null, null);          // Perform initial GUI update
            Show();

            lblScanStatus.Text = "Loading previous disk scan results...";
            ScanResultFile.Load();

            btnSelectFolder_Click(null, null);
        }

        private void btnSelectFolder_Click(object? sender, EventArgs? e)
        {
            var fbd = new FolderBrowserDialog();
            if (_currentScan != null)
            {
                lock (_currentScan)
                {
                    if (_currentScan.ScanRoot != null) fbd.SelectedPath = _currentScan.ScanRoot.FullName;
                }

                if (fbd.ShowDialog() != DialogResult.OK) return;

                if (_currentScan != null) _currentScan.Dispose();
            }

            _currentScan = new DiskScan(fbd.SelectedPath);

            GUITimer_Tick(null, null);          // Perform initial GUI update                       
        }

        private class PieEntry
        {
            public readonly string Label;
            public readonly double Gb;

            public PieEntry(string label, double gb) { Label = label; Gb = gb; }
        }

        private static string SizeString(long bytes)
        {
            return " (" + (new DataSize(bytes).ToFriendlyString(DataSize.Gigabyte)) + ")";
        }

        private void GUITimer_Tick(object? sender, EventArgs? e)
        {
            {
                if (_currentScan == null)
                {
                    PieChart.Visible = false;
                    lblFolderName.Text = "No folder selected.";
                    return;
                }

                PieChart.Visible = true;

                _currentScan.CheckHealth();
                lock (_currentScan)
                {
                    var sb = new StringBuilder();
                    sb.Append(_currentScan.FilesScanned + " files and " + _currentScan.FoldersScanned +
                              " folders scanned.  ");
                    switch (_currentScan.CurrentActivity)
                    {
                        case DiskScan.Activities.ScanningFolders:
                            sb.Append("Scanning folders...");
                            break;
                        case DiskScan.Activities.ScanningNewFolders:
                            sb.Append("Scanning files in new folders...");
                            break;
                        case DiskScan.Activities.RescanningOldFolders:
                            sb.Append("Rescanning files in folders from previous scans...");
                            break;
                        case DiskScan.Activities.CommittingPartialResults:
                            sb.Append("Committing partial scan results to database...");
                            break;
                        case DiskScan.Activities.CommittingFinalResults:
                            sb.Append("Committing final scan results to database...");
                            break;
                        case DiskScan.Activities.ScanComplete:
                            sb.Append("  Scan complete.");
                            break;
                    }

                    lblScanStatus.Text = sb.ToString();
                }

                PieChart.Series[0].Points.Clear();

                var topSummary = _currentScan.ScanRoot;
                if (topSummary == null)
                {
                    lblFolderName.Text = "Preparing scan...";
                    return;
                }

                var entries = new List<PieEntry>();

                const double gb = 1073741824;
                const double
                    tooSmallThresholdFraction =
                        0.05; // As a fraction of total size (this number sets the minimum pie slice size, which prevents overlapping text).            

                long unaccounted;
                long tooSmall = 0;
                lock (topSummary)
                {
                    lblFolderName.Text = topSummary.FullName;

                    long totalPieSize = 0;
                    if (cbRelativeToDisk.Checked && cbShowFreeSpace.Checked) totalPieSize = topSummary.Drive.TotalSize;
                    else if (cbRelativeToDisk.Checked)
                        totalPieSize = topSummary.Drive.TotalSize - topSummary.Drive.TotalFreeSpace;
                    else
                    {
                        foreach (var ds in topSummary.Subfolders)
                        {
                            if (ds != null)
                                lock (ds)
                                {
                                    totalPieSize += ds.Size;
                                }
                        }
                    }

                    unaccounted = topSummary.Drive.TotalSize;
                    var tooSmallThreshold = (long)(tooSmallThresholdFraction * totalPieSize);

                    if (cbRelativeToDisk.Checked && cbShowFreeSpace.Checked)
                        entries.Add(new PieEntry(
                            "Available Free Space" + SizeString(topSummary.Drive.AvailableFreeSpace),
                            topSummary.Drive.AvailableFreeSpace / gb));
                    unaccounted -= topSummary.Drive.AvailableFreeSpace;

                    foreach (var ds in topSummary.Subfolders)
                    {
                        if (ds != null)
                            lock (ds)
                            {
                                if (ds.Size < tooSmallThreshold)
                                {
                                    tooSmall += ds.Size;
                                    continue;
                                }

                                entries.Add(new PieEntry(ds.Name + SizeString(ds.Size), ds.Size / gb));
                                unaccounted -= ds.Size;
                            }
                    }
                }

                var dp = new DataPoint(2.0, tooSmall / gb)
                {
                    Label = "Other (Smaller Folders)" + SizeString(tooSmall)
                };
                PieChart.Series[0].Points.Add(dp);
                unaccounted -= tooSmall;

                if (cbRelativeToDisk.Checked)
                {
                    dp = new DataPoint(3.0, unaccounted / gb);
                    if (_currentScan.IsScanComplete)
                        dp.Label = "Outside this directory or inaccessible" + SizeString(unaccounted);
                    else
                        dp.Label = "Still Scanning...";
                    PieChart.Series[0].Points.Add(dp);
                }

                foreach (var pe in entries)
                {
                    dp = new DataPoint(0.0, pe.Gb)
                    {
                        Label = pe.Label
                    };
                    PieChart.Series[0].Points.Add(dp);
                }
            }
        }

        private void cbRelativeToDisk_CheckedChanged(object sender, EventArgs e)
        {
            GUITimer_Tick(null, null);
            cbShowFreeSpace.Enabled = cbRelativeToDisk.Checked;
        }

        private void cbShowFreeSpace_CheckedChanged(object sender, EventArgs e)
        {
            GUITimer_Tick(null, null);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _currentScan.Dispose();
        }
    }
}
