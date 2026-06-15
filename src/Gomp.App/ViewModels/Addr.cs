namespace Gomp.App.ViewModels;

/// <summary>
/// Address presentation helpers. E-addresses are long Base58 strings; the UI
/// shows a short head…tail form and gives each address a stable, vivid chat
/// colour (the Twitch coloured-username trick) derived deterministically from the
/// address so the same person is always the same colour.
/// </summary>
internal static class Addr
{
    // A dozen saturated hues that read well on the near-black surfaces.
    private static readonly string[] Palette =
    {
        "#FF5C8A", "#3FA7FF", "#2EE6A8", "#C98BFF", "#FF9F45", "#39E0E0",
        "#FFD23F", "#FF6B6B", "#8AB4FF", "#7DE08D", "#E879F9", "#5BD1C9",
    };

    public static string Short(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return "anon";
        return address.Length <= 12 ? address : $"{address[..6]}…{address[^4..]}";
    }

    public static string NameColor(string? address)
    {
        if (string.IsNullOrEmpty(address))
            return Palette[0];

        // FNV-1a over the address → stable bucket. Plain and deterministic; this
        // is cosmetic, not security, so a cheap hash is exactly right.
        uint hash = 2166136261;
        foreach (var c in address)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return Palette[(int)(hash % (uint)Palette.Length)];
    }
}
