using System.IO;
using System.Linq;

namespace Logzilla.Setup;

/// <summary>Measures an installed folder for the Add/Remove Programs size column.</summary>
public static class DirectorySize
{
    public static int InKilobytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var bytes = new DirectoryInfo(directory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);

        return (int)(bytes / 1024);
    }
}
