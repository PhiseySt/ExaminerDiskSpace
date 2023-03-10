using System.Globalization;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;

namespace ExaminerDiskSpace;

public class DirectorySummary : IXmlSerializable
{
    public string? Name;
    public string? FullName;
    public DataSize Size = 0;           // Size, including all subfolders & files
    public DateTime Oldest;             // Oldest date of user access of a file in this folder
    public DateTime Newest;             // Newest date of user access of a file in this folder
    public long TotalFiles;         // Count of number of files, including all files in subfolders
    public long TotalSubfolders;    // Count of number of subfolders, including all subfolders of subfolders
    public bool IsRoot;         // Indicates that the directory is the root of a file system (i.e. hard drive root)
    public List<DirectorySummary?> Subfolders = new();

    /// <summary>
    /// LastScanUtc indicates the completion time of the most recent scan.  If it is DateTime.MinValue, then the directory is
    /// listed as part of the hierarchy but has not been scanned at all, or the results are not being retained.  Note that
    /// when an update scan is being made, a fresh DirectorySummary object should be created so that any deleted Subfolders
    /// are not included.
    /// </summary>
    public DateTime LastScanUtc = DateTime.MinValue;

    [XmlIgnore]
    public bool HasBeenScanned => LastScanUtc > DateTime.MinValue;

    /// <summary>
    /// Indicates the parent of this directory.  A Parent can be null either when the directory is a root directory of a file system or
    /// when it is the highest level scanned in the current dataset.  Attaching the DirectorySummary to the ScanResultFile can alter
    /// this value, as it then becomes part of a larger hierarchy even if that did not come from the present scan.
    /// </summary>
    [XmlIgnore]
    public DirectorySummary? Parent;

    [XmlIgnore]
    private DriveInfo _mDrive;

    [XmlIgnore]
    public DriveInfo Drive
    {
        get
        {
            if (_mDrive == null)
            {
                if (FullName != null)
                {
                    DirectoryInfo di = new(FullName);
                    _mDrive = new DriveInfo(di.Root.FullName);
                }
            }
            return _mDrive;
        }
        set => _mDrive = value;
    }

    [XmlIgnore]
    public long TotalChildren => TotalFiles + TotalSubfolders;

    public DirectorySummary() { }       // For deserialization

    public DirectorySummary(DirectorySummary? parent, DirectoryInfo di)
    {
        Name = di.Name;
        FullName = di.FullName;
        Oldest = di.LastWriteTime;
        Newest = di.LastWriteTime;
        TotalFiles = 0;
        IsRoot = di.Parent == null;
        Parent = parent;
        if (IsRoot) Drive = new DriveInfo(FullName);
    }

    public class DeltaCounters
    {
        public DataSize Size;
        public long TotalFiles, TotalSubfolders;

        public static DeltaCounters operator +(DeltaCounters a, DeltaCounters b)
        {
            var ret = new DeltaCounters
            {
                Size = a.Size + b.Size,
                TotalFiles = a.TotalFiles + b.TotalFiles,
                TotalSubfolders = a.TotalSubfolders + b.TotalSubfolders
            };
            return ret;
        }
    }

    public void Adjust(DeltaCounters delta)
    {
        Size += delta.Size;
        TotalFiles += delta.TotalFiles;
        TotalSubfolders += delta.TotalSubfolders;

        // Oldest and Newest are invalid at this time.  They need to be retabulated from the bottom-up.
        Oldest = DateTime.MaxValue;
        Newest = DateTime.MinValue;
    }

    #region "Memory and Disk Conservation Rules (Culling)"

    private static readonly DataSize MinimumFolderSize = DataSize.Gigabyte;
    private const int MinimumChildCount = 250;

    [XmlIgnore]
    public bool ShouldCull => Size < MinimumFolderSize && TotalChildren < MinimumChildCount;

    public void CullDetails()
    {
        try
        {
            if (IsRoot) Size = Drive.TotalSize - Drive.AvailableFreeSpace;

            for (var i = 0; i < Subfolders.Count;)
            {
                if (Subfolders[i]!.ShouldCull)
                {
                    Subfolders.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }
        catch (Exception ex) { throw new Exception("While culling folder information details: " + ex.Message, ex); }
    }

    #endregion

    #region "IXmlSerializable implementation"

    private bool ContainsScanData
    {
        get
        {
            if (LastScanUtc > DateTime.MinValue && !ShouldCull) return true;
            foreach (var ds in Subfolders)
            {
                if (ds is { ContainsScanData: true }) return true;
            }
            return false;
        }
    }

    public XmlSchema? GetSchema() { return null; }

    private static readonly CultureInfo XmlCulture = CultureInfo.CreateSpecificCulture("en-US");

    public void WriteXml(XmlWriter writer)
    {
        writer.WriteAttributeString("Name", Name);
        writer.WriteAttributeString("FullName", FullName);
        writer.WriteAttributeString("SizeInBytes", Size.Size.ToString());
        writer.WriteAttributeString("Oldest", Oldest.ToUniversalTime().ToString("u", XmlCulture));
        writer.WriteAttributeString("Newest", Newest.ToUniversalTime().ToString("u", XmlCulture));
        writer.WriteAttributeString("TotalFiles", TotalFiles.ToString());
        writer.WriteAttributeString("TotalSubfolders", TotalSubfolders.ToString());
        if (IsRoot) writer.WriteAttributeString("IsRoot", IsRoot.ToString());
        if (LastScanUtc > DateTime.MinValue) writer.WriteAttributeString("LastScanUtc", LastScanUtc.ToUniversalTime().ToString("u", XmlCulture));

        var startedList = false;
        foreach (var subfolder in Subfolders)
        {
            if (subfolder is { ContainsScanData: true })
            {
                if (!startedList)
                {
                    writer.WriteStartElement("Subfolders");
                    startedList = true;
                }

                writer.WriteStartElement("DirectorySummary");
                ((IXmlSerializable)subfolder).WriteXml(writer);
                writer.WriteEndElement();
            }
        }
        if (startedList) writer.WriteEndElement();
    }

    public void ReadXml(XmlReader reader)
    {
        const DateTimeStyles utcStyle = DateTimeStyles.AdjustToUniversal;

        reader.MoveToContent();
        Name = reader.GetAttribute("Name");
        FullName = reader.GetAttribute("FullName");
        Size = long.Parse(reader.GetAttribute("SizeInBytes")!);
        Oldest = DateTime.Parse(reader.GetAttribute("Oldest")!, XmlCulture, utcStyle);
        Newest = DateTime.Parse(reader.GetAttribute("Newest")!, XmlCulture, utcStyle);
        TotalFiles = long.Parse(reader.GetAttribute("TotalFiles")!);
        TotalSubfolders = long.Parse(reader.GetAttribute("TotalSubfolders")!);
        var isRootString = reader.GetAttribute("IsRoot");
        IsRoot = isRootString != null && bool.Parse(isRootString);
        var lastScanUtcString = reader.GetAttribute("LastScanUtc");
        LastScanUtc = lastScanUtcString == null ? DateTime.MinValue : DateTime.Parse(lastScanUtcString, XmlCulture, utcStyle);

        var isEmpty = reader.IsEmptyElement;
        reader.ReadStartElement();
        if (isEmpty) return;

        for (; ; )
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "DirectorySummary") { reader.ReadEndElement(); return; }

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "Subfolders" && !reader.IsEmptyElement)
            {
                reader.ReadStartElement();          // Read <Subfolders>
                for (; ; )
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "Subfolders") { reader.ReadEndElement(); break; }

                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "DirectorySummary")
                    {
                        var dsChild = new DirectorySummary();
                        dsChild.ReadXml(reader);
                        Subfolders.Add(dsChild);
                    }
                    else reader.Read();
                }
            }
            else reader.Read();
        }
    }
    #endregion

    private static int CompareByLargest(DirectorySummary? a, DirectorySummary? b)
    {
        // Return (-1) b is greater, (0) Equal, (+1) a is greater.

        if (a == null)
        {
            if (b == null) return 0;
            return -1;
        }

        if (b == null) return 1;
        if (a.Size == b.Size) return 0;
        return a.Size < b.Size ? 1 : -1;
    }

    private static int CompareByOldest(DirectorySummary? a, DirectorySummary? b)
    {
        // Return (-1) b is greater, (0) Equal, (+1) a is greater.

        if (a == null)
        {
            if (b == null) return 0;
            return -1;
        }

        if (b != null && a.Newest == b.Newest) return 0;
        // Since we are looking for the Oldest, being newest is not greater...
        // However, "greater" seems to be the opposite sorting order that we want,
        // so we flip again.
        return b != null && a.Newest > b.Newest ? 1 : -1;
    }

    [XmlIgnore]
    public string DisplayName
    {
        get
        {
            if (IsRoot)
            {
                var vl = Drive.VolumeLabel;
                if (vl.Length > 0) return vl;
                return Drive.RootDirectory.FullName + " Drive";
            }

            if (Name == "$RECYCLE.BIN") return "Recycle Bin";
            return "\\" + Name;
        }
    }

    [XmlIgnore]
    public DataSize DisplaySize
    {
        get
        {
            if (IsRoot)
            {
                return Drive.TotalSize - Drive.AvailableFreeSpace;
            }

            return Size;
        }
    }

    public override string ToString()
    {
        return FullName + " (" + Size.ToFriendlyString() + ")";
    }
}