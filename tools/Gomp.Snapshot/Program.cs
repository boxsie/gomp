using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Gomp.App.Services;
using Gomp.App.ViewModels;
using Gomp.Client;
using Gomp.Protocol;

namespace Gomp.Snapshot;

// Headless Skia snapshotter: renders gomp's screens to PNGs so the design can be
// eyeballed without a display (GNOME Wayland blocks gdbus screenshots).
//   dotnet run --project tools/Gomp.Snapshot -- <out-dir>
internal static class Program
{
    private const int W = 1180;
    private const int H = 760;

    [STAThread]
    public static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<Gomp.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Render(BuildConnect(), Path.Combine(outDir, "01-connect.png"));
        Render(BuildRoom(), Path.Combine(outDir, "02-room.png"));
        Render(BuildManage(), Path.Combine(outDir, "03-manage.png"));
        // Short window: proves the docked footer never lets the body bleed under it.
        Render(BuildManage(), Path.Combine(outDir, "03b-manage-short.png"), 580);
        Console.WriteLine("snapshots written to " + Path.GetFullPath(outDir));
    }

    private static MainWindowViewModel BuildManage()
    {
        var vm = BuildRoom();
        var room = vm.Rooms.First(r => r.IsOwner);
        vm.SelectedRoom = room;
        room.ManageCommand.Execute(null);   // opens the overlay + loads detail (SampleGateway)
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static void Render(MainWindowViewModel vm, string path, int height = H)
    {
        var window = new Gomp.App.Views.MainWindow { DataContext = vm, Width = W, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        frame?.Save(path);
        window.Close();
        Console.WriteLine("wrote " + path);
    }

    private static MainWindowViewModel BuildConnect()
    {
        var vm = new MainWindowViewModel(new SampleGateway(), new SyncDispatcher());
        return vm;
    }

    private static MainWindowViewModel BuildRoom()
    {
        var gw = new SampleGateway();
        var vm = new MainWindowViewModel(gw, new SyncDispatcher());
        vm.ConnectCommand.Execute(null);

        // A few rooms in the rail.
        Create(vm, "general", RoomKind.Invite);
        Create(vm, "music", RoomKind.Open);
        Create(vm, "dev-talk", RoomKind.Friends);

        // Light up the sidebar dots for the other rooms.
        Push(gw, "Eroom-music", r => Roster(r, ("Ekz8Qa", true), ("Ep2Lm9", true)));
        Push(gw, "Eroom-dev-talk", r => Roster(r, ("Eaa11", false)));

        // Focus "general" and populate a believable conversation.
        var general = vm.Rooms.First(r => r.Title == "general");
        vm.SelectedRoom = general;
        var obs = (IRoomObserver)general;
        Roster(obs,
            ("Eself9mQ2kP7vX", true),
            ("Ekz8QaWmР3", true),
            ("Ep2Lm9TtZ", true),
            ("Eqr7BvN0", false));

        long t = 1718500000000;
        obs.OnPostAsync(P("Ekz8QaWmР3", "anyone around to test the new build?", 1, t, PostTrust.Verified));
        obs.OnPostAsync(P("Ep2Lm9TtZ", "yep — pulling it now", 2, t + 40_000, PostTrust.Verified));
        obs.OnPostAsync(P("Eself9mQ2kP7vX", "pushed the fix to main, should be clean", 3, t + 95_000, PostTrust.Verified));
        obs.OnPostAsync(P("Eqr7BvN0", "this message claims to be from someone it isn't", 4, t + 130_000, PostTrust.Forged));
        obs.OnPostAsync(P("Ekz8QaWmР3", "nice, signatures verify on my end ✦", 5, t + 175_000, PostTrust.Verified));
        general.AddSystem("removed Eqr7Bv…N0 from the room");

        return vm;
    }

    private static void Create(MainWindowViewModel vm, string name, RoomKind kind)
    {
        vm.ShowCreateCommand.Execute(null);
        vm.CreateName = name;
        vm.CreateKind = kind;
        vm.ConfirmCreateCommand.Execute(null);
    }

    private static void Push(SampleGateway gw, string addr, Action<IRoomObserver> act)
    {
        if (gw.Observers.TryGetValue(addr, out var obs))
            act(obs);
    }

    private static void Roster(IRoomObserver obs, params (string addr, bool online)[] members) =>
        obs.OnRosterAsync(members.Select(m => new RoomMemberPresence(m.addr, m.online)).ToList());

    private static ReceivedPost P(string sender, string text, long seq, long ts, PostTrust trust) =>
        new(seq, ts, sender, System.Text.Encoding.UTF8.GetBytes(text), ts, "n" + seq, trust);
}
