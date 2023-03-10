using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExaminerDiskSpace;

public class FileUtility
{
    protected const uint FileAttributeReparsePoint = 0x400;

    public static long GetFileSizeOnDisk(FileInfo info)
    {
        var fattr = GetFileAttributesW(info.FullName);
        if ((fattr & FileAttributeReparsePoint) != 0) throw new Exception("Unable to determine file size for a reparse point.");

        var result = GetDiskFreeSpaceW(info.Directory.Root.FullName, out var sectorsPerCluster, out var bytesPerSector, out _, out _);
        if (result == 0) throw new Win32Exception(result);
        var clusterSize = sectorsPerCluster * bytesPerSector;
        var losize = GetCompressedFileSizeW(info.FullName, out var hosize);
        long size;
        size = (long)hosize << 32 | losize;
        return size + clusterSize - 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCompressedFileSizeW([In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
       [Out, MarshalAs(UnmanagedType.U4)] out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true, PreserveSig = true)]
    private static extern int GetDiskFreeSpaceW([In, MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName,
       out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters,
       out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetFileAttributesW([In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName);
}