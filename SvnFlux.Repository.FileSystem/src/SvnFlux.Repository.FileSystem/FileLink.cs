using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SvnFlux.Repository.FileSystem;

internal static partial class FileLink
{
    public static void CreateHardLink(string path, string existingFilePath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(path, existingFilePath, 0))
            {
                throw new IOException(
                    $"Could not create hard link '{path}' to '{existingFilePath}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return;
        }

        if (CreateHardLinkUnix(existingFilePath, path) != 0)
        {
            throw new IOException(
                $"Could not create hard link '{path}' to '{existingFilePath}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreateHardLinkUnix(string existingFileName, string fileName);
}
