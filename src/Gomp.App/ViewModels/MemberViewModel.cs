using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Gomp.App.ViewModels;

/// <summary>
/// One person in a room's member list. Presence flips live, so
/// <see cref="Online"/> is observable; the rest is fixed per member. The remove
/// affordance is only wired (and only shown) when the signed-in member owns the
/// room and removal is possible.
/// </summary>
public sealed partial class MemberViewModel : ObservableObject
{
    private readonly Func<MemberViewModel, Task>? _remove;

    public MemberViewModel(string address, bool online, bool isSelf, bool canRemove,
        Func<MemberViewModel, Task>? remove = null)
    {
        Address = address;
        DisplayName = Addr.Short(address);
        NameColorHex = isSelf ? "#BF94FF" : Addr.NameColor(address);
        IsSelf = isSelf;
        CanRemove = canRemove && !isSelf;
        _remove = remove;
        _online = online;
    }

    public string Address { get; }
    public string DisplayName { get; }
    public string NameColorHex { get; }
    public bool IsSelf { get; }

    /// <summary>Owner-only, invite-room-only, never yourself — drives the remove button.</summary>
    public bool CanRemove { get; }

    [ObservableProperty]
    private bool _online;

    [RelayCommand]
    private Task RemoveAsync() => _remove is null ? Task.CompletedTask : _remove(this);
}
