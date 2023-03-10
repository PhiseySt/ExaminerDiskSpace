using System.Xml.Serialization;

namespace ExaminerDiskSpace;

public class ScanResultFile
{
    /// <summary>
    /// Roots contains a hierarchal listing of all previous results that are retained (smaller folders are reduced to a
    /// summary).  There can be more than one entry at the top level of Roots as there can be more than one top-level
    /// scan conducted (i.e. multiple hard drives).
    /// </summary>
    public List<DirectorySummary?> Roots = new();

    private static readonly XmlSerializer Serializer = new(typeof(ScanResultFile));

    private void SerializeTo(Stream str) { Serializer.Serialize(str, this); }

    private static ScanResultFile? Deserialize(Stream str) { return Serializer.Deserialize(str) as ScanResultFile; }

    public static ScanResultFile? OpenFile;
    public static void Load()
    {
        try
        {
            var scanResultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ExaminerDiskSpace_Database.xml");
            using var fs = new FileStream(scanResultPath, FileMode.Open);
            OpenFile = Deserialize(fs);
        }
        catch (Exception)
        {
            OpenFile = new ScanResultFile();
        }
    }
    public static void Save()
    {
        var scanResultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExaminerDiskSpace_Database.xml");
        using (var fs = new FileStream(scanResultPath, FileMode.Create)) OpenFile?.SerializeTo(fs);

#if DEBUG
        // Serialize to a MemoryStream, Deserialize it, and Reserialize it again - then make sure it is identical to the original.
        // This validation ensures that our IXmlSerializable custom ReadXml() and WriteXml() are consistent and repeatable.

        try
        {
            using var s1 = new MemoryStream();
            OpenFile?.SerializeTo(s1);
            s1.Seek(0, SeekOrigin.Begin);

            var fromS1 = Deserialize(s1);
            using var s2 = new MemoryStream();
            fromS1?.SerializeTo(s2);

            s1.Seek(0, SeekOrigin.Begin);
            s2.Seek(0, SeekOrigin.Begin);
            var buf1 = new byte[4096];
            var buf2 = new byte[4096];
            for (; ; )
            {
                var count1 = s1.Read(buf1, 0, buf1.Length);
                var count2 = s2.Read(buf2, 0, buf2.Length);
                if (count1 != count2) throw new Exception("Verification of serialization-deserialization failed due to different lengths.");

                for (var i = 0; i < count1; i++)
                {
                    if (buf1[i] != buf2[i]) throw new Exception("Verification of serialization-deserialization failed due to mismatch.");
                }

                if (count1 < buf1.Length) break;        // EOF reached.
            }
        }
        catch (Exception ex)
        {
            string verifyPath;
            try
            {
                Load();
                verifyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExaminerDiskSpace_Database.verify.xml");
                using var fs = new FileStream(verifyPath, FileMode.Create);
                OpenFile?.SerializeTo(fs);
            }
            catch (Exception)
            {
                throw ex;
            }
            throw new Exception(ex.Message + "\n\nTo compare the original serialization to the deserialize-serialize, compare the database file (" + scanResultPath + ") to the verification file (" + verifyPath + ").", ex);
        }
#endif

    }

    /// <summary>
    /// Find(FullName) locates the object in the hierarchy that corresponds to the requested path.  The search
    /// is not case sensitive.  Null is returned if the object is not found in the existing hierarchy.
    /// </summary>
    /// <param name="fullName"></param>
    /// <returns>The DirectorySummary corresponding to the requested path name.</returns>
    public DirectorySummary? Find(string? fullName)
    {
        var parentFullName = Path.GetDirectoryName(fullName);
        if (parentFullName == null)
        {
            // We have found a root (or an error).
            foreach (var root in Roots)
            {
                if (root != null && root.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)) return root;
            }
            return null;            // The requested entry does not exist in the tree.
        }

        var dsParent = Find(parentFullName);
        if (dsParent == null) return null;
        foreach (var child in dsParent.Subfolders)
        {
            if (child != null && child.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)) return child;
        }
        return null;            // The requested entry does not exist in the tree.
    }

    /// <summary>
    /// MergeResults replaces or adds NewResults to the existing hierarchy.  Placeholder DirectorySummary entries
    /// will be created as necessary in order to attach the new entry to a root entry.  If the directory already
    /// exists in the hierarchy, it is replaced by NewResults.  NewResults.Parent may be updated.
    /// </summary>
    /// <param name="newResults">The directory to add/update to the scan result file hierarchy.</param>
    public void MergeResults(DirectorySummary? newResults)
    {
        if (newResults is { IsRoot: true })
        {
            for (var i = 0; i < Roots.Count;)
            {
                if (ReferenceEquals(Roots[i], newResults)) return;         // Already merged.
                if (Roots[i]!.FullName.Equals(newResults.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    // We have found an existing entry for this path.  Replace it with our new results.
                    Roots.RemoveAt(i);
                }
                else i++;
            }
            // The NewResults for the new results is not in the hierarchy.  
            Roots.Add(newResults);
            return;
        }

        // So NewResults is not a root.  We may need to construct DirectorySummary objects (with LastScanUtc marked as MinValue, never scanned) until
        // we can attach this directory to an existing tree or root.  For each level, we have to check whether the directory exists or a placeholder 
        // is needed, then we can work up.

        var parentPath = Path.GetDirectoryName(newResults?.FullName);
        var existingParent = Find(parentPath);
        if (existingParent != null)
        {
            for (var i = 0; i < existingParent.Subfolders.Count;)
            {
                if (ReferenceEquals(existingParent.Subfolders[i], newResults)) return;         // Already merged.
                if (existingParent.Subfolders[i]!.FullName.Equals(newResults?.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    // Remove it.  Even if it happens to be pointing to the object we're trying to add, we'll re-add it in a moment.
                    existingParent.Subfolders.RemoveAt(i);
                }
                else i++;
            }
            existingParent.Subfolders.Add(newResults);
            return;
        }

        // The parent is not present in the hierarchy.  We'll need to add at least one layer of unscanned directory in order to advance up a level and repeat.
        if (parentPath != null)
        {
            var unscanned = new DirectorySummary(null, new DirectoryInfo(parentPath));
            unscanned.Subfolders.Add(newResults);
            // Note: we don't bother merging counts (i.e. calling Unscanned.MergeChild()) into the unscanned because it is incomplete anyway.  It's just a 
            //       hierarchy placeholder.
            MergeResults(unscanned);
        }
    }
}