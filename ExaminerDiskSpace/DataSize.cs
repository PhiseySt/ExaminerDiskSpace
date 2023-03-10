namespace ExaminerDiskSpace;

/** This class can also be found in Wiley Black's Software Library **/

/// <summary>
/// The DataSize structure represents a bytewise data length, such as the size
/// of a file, a network packet, RAM, etc.  The structure provides tools to aid
/// in the optimal presentation of this data length, such as printing a 1.3 GB file
/// as "1.35 GB", "1.3 GB", "1 GB", etc., depending on options.  A Parse()
/// utility is also providing for the same purpose.  Note that the default ToString()
/// implementation always provides an exact representation such as "1928386 bytes"
/// in case it is used for information storage or processing.  See the 
/// ToFriendlyString() and Parse() methods for more information.
/// </summary>
public struct DataSize
{
    private const long GKilobyte = 1024;
    private const long GMegabyte = 1048576;
    private const long GGigabyte = 1073741824;
    private const long GTerrabyte = 1099511627776;

    public static DataSize Kilobyte = new(GKilobyte);
    public static DataSize Megabyte = new(GMegabyte);
    public static DataSize Gigabyte = new(GGigabyte);
    public static DataSize Terrabyte = new(GTerrabyte);

    public long Size;

    public DataSize(long size) { Size = size; }

    public static DataSize operator +(DataSize a, DataSize b) { return new DataSize(a.Size + b.Size); }
    public static DataSize operator -(DataSize a, DataSize b) { return new DataSize(a.Size - b.Size); }
    public static implicit operator DataSize(long a) { return new DataSize(a); }
    public static implicit operator long(DataSize a) { return a.Size; }
    public override bool Equals(object? obj)
    {
        if (obj is not DataSize size) return false;
        return Size == size.Size;
    }
    public static bool operator >(DataSize a, DataSize b) { return a.Size > b.Size; }
    public static bool operator <(DataSize a, DataSize b) { return a.Size < b.Size; }
    public static bool operator >=(DataSize a, DataSize b) { return a.Size >= b.Size; }
    public static bool operator <=(DataSize a, DataSize b) { return a.Size <= b.Size; }

    public static bool operator ==(DataSize left, DataSize right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DataSize left, DataSize right)
    {
        return !(left == right);
    }

    /// <summary>
    /// The ToString() method returns an exact representation of the
    /// data size, such as "1932964 bytes".
    /// </summary>
    /// <returns>A string representation of the data size.</returns>
    public override string ToString()
    {
        return Size + " bytes";
    }

    /// <summary>
    /// ToFriendlyString() provides a human friendly presentation of
    /// the data size.  For example, FileLength.ToFriendlyString()
    /// might return "1.3 GB".
    /// </summary>        
    /// <returns>An inexact human readable string which can also be handled by Parse().</returns>
    public string ToFriendlyString() { return ToFriendlyString(GGigabyte, 1); }

    /// <summary>
    /// ToFriendlyString() provides a human friendly presentation of
    /// the data size.  For example, FileLength.ToFriendlyString(DataSize.Gigabyte)
    /// might return "1.3 GB".
    /// </summary>
    /// <param name="fractionThreshold">The smallest data size for which a fractional digit will be included.</param>
    /// <returns>An inexact human readable string which can also be handled by Parse().</returns>
    public string ToFriendlyString(long fractionThreshold) { return ToFriendlyString(fractionThreshold, 1); }

    /// <summary>
    /// ToFriendlyString() provides a human friendly presentation of
    /// the data size.  For example, FileLength.ToFriendlyString(DataSize.Gigabyte)
    /// might return "1.3 GB".
    /// </summary>
    /// <param name="fractionThreshold">The smallest data size for which a fractional digit will be included.</param>
    /// <param name="fractionalDigits">The number of fractional digits to include when FractionThreshold is exceeded.</param>
    /// <returns>An inexact human readable string which can also be handled by Parse().</returns>
    public string ToFriendlyString(long fractionThreshold, int fractionalDigits)
    {
        string postfix;
        double divisor;
        if (Size < GKilobyte) { postfix = " bytes"; divisor = 1.0; }
        else if (Size < GMegabyte) { postfix = " KB"; divisor = GKilobyte; }
        else if (Size < GGigabyte) { postfix = " MB"; divisor = GMegabyte; }
        else if (Size < GTerrabyte) { postfix = " GB"; divisor = GGigabyte; }
        else { postfix = " TB"; divisor = GTerrabyte; }

        if (Size < fractionThreshold) return ((long)Math.Round(Size / divisor)) + postfix;
        return (Size / divisor).ToString("F0" + fractionalDigits) + postfix;
    }

    public enum Unit
    {
        Bytes,
        Kilobytes,
        Megabytes,
        Gigabytes,
        Terrabytes
    }

    /// <summary>
    /// <para>The DataSize.Parse() function accepts a string in any of the following formats:</para>
    /// <list type="bullet">
    /// <item>Exact numeric string such as "1392853".</item>
    /// <item>Exact string with the "bytes" postfix such as "1024 bytes" or "1024bytes".</item>
    /// <item>Possibly inexact string with a postfix of "KB", "MB", "GB", or "TB".  The numeric 
    ///     value can contain a fraction, as in "1.359 GB".</item>
    /// </list>
    /// </summary>
    /// <param name="str">The string to be parsed.</param>
    /// <returns>A DataSize object representing the value.  If the string cannot be parsed, an exception is thrown</returns>
    public static DataSize Parse(string str) { return Parse(str, Unit.Bytes); }

    /// <summary>
    /// <para>The DataSize.Parse() function accepts a string in any of the following formats:</para>
    /// <list type="bullet">        
    /// <item>Exact string with the "bytes" postfix such as "1024 bytes" or "1024bytes".</item>
    /// <item>Possibly exact numeric string such as "1.359" or "29394", which will take the units specified
    ///     by the DefaultUnit parameter.</item>
    /// <item>Possibly inexact string with a postfix of "KB", "MB", "GB", or "TB".  The numeric 
    ///     value can contain a fraction, as in "1.359 GB".</item>
    /// </list>
    /// </summary>
    /// <param name="str">The string to be parsed.</param>
    /// <param name="defaultUnit">The default units to be applied when the string contains only a numeric value.</param>
    /// <returns>A DataSize object representing the value.  If the string cannot be parsed, an exception is thrown</returns>
    public static DataSize Parse(string str, Unit defaultUnit)
    {
        str = str.Trim();
        string working;
        double factor;
        int iIndex;
        if ((iIndex = str.IndexOf("TB", StringComparison.Ordinal)) >= 0) { factor = GTerrabyte; working = str[..iIndex]; }
        else if ((iIndex = str.IndexOf("GB", StringComparison.Ordinal)) >= 0) { factor = GGigabyte; working = str[..iIndex]; }
        else if ((iIndex = str.IndexOf("MB", StringComparison.Ordinal)) >= 0) { factor = GMegabyte; working = str[..iIndex]; }
        else if ((iIndex = str.IndexOf("B", StringComparison.Ordinal)) >= 0 || (iIndex = str.IndexOf("bytes", StringComparison.Ordinal)) >= 0) { factor = 1.0; working = str[..iIndex]; }
        else
        {
            working = str;
            factor = defaultUnit switch
            {
                Unit.Bytes => 1.0,
                Unit.Kilobytes => GKilobyte,
                Unit.Megabytes => GMegabyte,
                Unit.Gigabytes => GGigabyte,
                Unit.Terrabytes => GTerrabyte,
                _ => throw new NotSupportedException()
            };
        }

        var size = double.Parse(working.TrimEnd());
        return new DataSize((long)(size * factor));
    }

    public override int GetHashCode() { return Size.GetHashCode(); }

}