using System.Text;
using Gomp.Client;

namespace Gomp.App.ViewModels;

/// <summary>
/// One row in a pub's chat — either a member's post (with its authorship trust)
/// or a centred system line (errors, barring notices). Immutable: a post's trust
/// is fixed at delivery, so nothing here changes after construction.
/// </summary>
public sealed class MessageViewModel
{
    private MessageViewModel(
        long seq, string sender, string displayName, string content,
        DateTimeOffset timestamp, PostTrust trust, bool isSelf, bool isSystem)
    {
        Seq = seq;
        DisplayName = displayName;
        Content = content;
        Timestamp = timestamp;
        Trust = trust;
        IsSelf = isSelf;
        IsSystem = isSystem;
        // A forged post shows its CLAIMED author in danger-red — the name is a
        // lie the host is telling, so it shouldn't wear a friendly colour.
        NameColorHex = trust == PostTrust.Forged
            ? "#EB0400"
            : isSelf ? "#BF94FF" : Addr.NameColor(sender);
    }

    public long Seq { get; }
    public string DisplayName { get; }
    public string Content { get; }
    public DateTimeOffset Timestamp { get; }
    public PostTrust Trust { get; }
    public bool IsSelf { get; }
    public bool IsSystem { get; }
    public string NameColorHex { get; }

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    public bool IsForged => Trust == PostTrust.Forged;
    public bool IsVerified => Trust == PostTrust.Verified;

    /// <summary>Forged posts are dimmed so they read as suspect at a glance.</summary>
    public double RowOpacity => IsForged ? 0.6 : 1.0;

    /// <summary>Show a badge for everything except the quiet host-trusted case.</summary>
    public bool ShowTrustBadge => !IsSystem && Trust != PostTrust.Unverified;

    public string TrustGlyph => Trust switch
    {
        PostTrust.Verified => "✓",
        PostTrust.Forged => "⚠",
        _ => "",
    };

    public string TrustColorHex => Trust switch
    {
        PostTrust.Verified => "#2DC97E",
        PostTrust.Forged => "#EB0400",
        _ => "#8A8A95",
    };

    public string TrustTooltip => Trust switch
    {
        PostTrust.Verified => "Verified — signed by this member; the host can't forge it.",
        PostTrust.Forged => "Forged — the signature didn't match this member's key. Treat as fake.",
        _ => "Unverified — attributed by the host; no signature checked yet.",
    };

    public static MessageViewModel FromPost(ReceivedPost post, string selfAddress)
    {
        var text = Encoding.UTF8.GetString(post.Content);
        var ts = post.HostTs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(post.HostTs)
            : DateTimeOffset.FromUnixTimeMilliseconds(post.ClientTs);
        return new MessageViewModel(
            post.Seq, post.Sender, Addr.Short(post.Sender), text, ts,
            post.Trust, isSelf: post.Sender == selfAddress, isSystem: false);
    }

    public static MessageViewModel System(string text) =>
        new(seq: long.MaxValue, sender: "", displayName: "", content: text,
            timestamp: DateTimeOffset.UtcNow, trust: PostTrust.Unverified,
            isSelf: false, isSystem: true);
}
