namespace Gomp.Host;

/// <summary>
/// Write-to-temp-then-rename helper so a crash mid-write can never leave a
/// half-written state file (a torn admins.json / rooms.json would strand the
/// host on next boot). The rename is atomic on the same filesystem, which the
/// service data dir always is.
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
