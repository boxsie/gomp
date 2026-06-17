using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Ensemble.Client;
using Gomp.Client;
using Gomp.Host;
using Gomp.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Gomp.Integration;

/// <summary>
/// The live two-party end-to-end proof (ticket 614b816f): a real
/// <see cref="RoomHost"/> and two <see cref="GompClient"/> members, each on its
/// own real <c>ensemble --signaling=loopback</c> daemon, exercising the whole
/// room story over the wire — create, join, post, fan-out, since-cursor backfill,
/// presence, and the verifiable-authorship path (Verified / Unverified / Forged).
///
/// Topology (three daemons, all loopback, one rendezvous dir):
/// <list type="bullet">
///   <item>dHost — the host's daemon. <see cref="RoomHost"/> runs in-process
///   against it (tokenless operator socket; Case 3 registration). In-process is
///   as faithful to the WIRE as the supervised exe — same daemon, same services,
///   same fan-out — and is the only way to drive a MALICIOUS-host fan-out, which
///   is the sole on-wire source of a Forged post (an honest submit is
///   sender-checked).</item>
///   <item>dA — member A's daemon. A is the room owner (admin authority).</item>
///   <item>dB — member B's daemon. Two members need two daemons: the same
///   `gomp` service name on one daemon would collide on one identity.</item>
/// </list>
///
/// Gated: dormant unless <c>GOMP_E2E=1</c> and an ensemble binary resolves
/// (<c>ENSEMBLE_BIN</c> or <c>../ensemble/bin/ensemble</c>) — the C# analogue of
/// the SDK's <c>-tags integration</c>.
/// </summary>
public sealed class LiveTwoPartyTests
{
    private readonly ITestOutputHelper _out;
    public LiveTwoPartyTests(ITestOutputHelper o) => _out = o;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MsgTimeout = TimeSpan.FromSeconds(15);

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task FullRoomLifecycle_TwoMembers_VerifiableAuthorship()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("GOMP_E2E") == "1",
            "live daemon e2e — set GOMP_E2E=1 to run");
        var bin = DaemonProcess.LocateBinary()
            ?? throw new SkipException("ensemble binary not found (set ENSEMBLE_BIN or build ../ensemble/bin/ensemble)");
        _out.WriteLine($"ensemble binary: {bin}");

        var rdv = Directory.CreateTempSubdirectory("gomp-rdv-").FullName;
        var disposables = new Stack<IAsyncDisposable>();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        try
        {
            // --- daemons ---
            var dHost = await DaemonProcess.StartAsync("host", bin, rdv, ReadyTimeout);
            disposables.Push(dHost);
            var dA = await DaemonProcess.StartAsync("memberA", bin, rdv, ReadyTimeout);
            disposables.Push(dA);
            var dB = await DaemonProcess.StartAsync("memberB", bin, rdv, ReadyTimeout);
            disposables.Push(dB);
            _out.WriteLine("three loopback daemons ready");

            // --- members (identities first; the host's base ACL is owner-gated,
            //     so we need A's address before we can stand the host up) ---
            var clientA = new EnsembleClient(dA.Endpoint, loggerFactory.CreateLogger<EnsembleClient>());
            disposables.Push(clientA);
            var gompA = await GompClient.ConnectAsync(clientA, MemberManifest());
            disposables.Push(gompA);

            var clientB = new EnsembleClient(dB.Endpoint, loggerFactory.CreateLogger<EnsembleClient>());
            disposables.Push(clientB);
            var gompB = await GompClient.ConnectAsync(clientB, MemberManifest());
            disposables.Push(gompB);

            var aAddr = gompA.Address;
            var bAddr = gompB.Address;
            _out.WriteLine($"member A = {Short(aAddr)}");
            _out.WriteLine($"member B = {Short(bAddr)}");
            Assert.NotEqual(aAddr, bAddr); // distinct identities

            // --- host in-process (owner = A) ---
            var hostClient = new EnsembleClient(dHost.Endpoint, loggerFactory.CreateLogger<EnsembleClient>());
            disposables.Push(hostClient);
            var hostDataDir = Directory.CreateTempSubdirectory("gomp-host-").FullName;
            var host = new RoomHost(hostClient, "gomp", hostDataDir, aAddr, 1000, loggerFactory);
            disposables.Push(host);
            await host.StartAsync();
            var hostBase = host.BaseAddress!;
            _out.WriteLine($"host base = {Short(hostBase)}");

            // ===== 1. create a room (admin op A→base; proves the owner-gated dial) =====
            var createTask = gompA.CreateRoomAsync(hostBase, "general", RoomKind.Open);
            if (await Task.WhenAny(createTask, Task.Delay(LinkTimeout)) != createTask)
            {
                _out.WriteLine("CreateRoom (A→base admin dial) STALLED — route diagnosis:");
                Dump(dA, dHost);
                _out.WriteLine($"--- memberA debug ---\n{await dA.DebugInfoAsync(bin)}");
                _out.WriteLine($"--- host debug ---\n{await dHost.DebugInfoAsync(bin)}");
                throw new TimeoutException("CreateRoom stalled (A→base never completed)");
            }
            var created = await createTask;
            Assert.True(created.Ok, $"CreateRoom failed: {created.Error}");
            var roomAddr = created.Rooms.Single(r => r.Name == "general").Addr;
            _out.WriteLine($"room 'general' = {Short(roomAddr)}");

            // ===== 2. A joins and posts two messages BEFORE B joins (sets up backfill) =====
            var obsA = new Recorder(_out, "A");
            var sessA = await gompA.JoinRoomAsync(roomAddr, obsA);
            await WaitUntil(() => obsA.Has(p => p.online && p.addr == aAddr) || obsA.RosterHas(aAddr),
                LinkTimeout, "A sees itself online", () => Dump(dA, dHost));

            await sessA.SendChatAsync("msg1");
            await sessA.SendChatAsync("msg2");
            await WaitUntil(() => obsA.PostByContent("msg1") is not null && obsA.PostByContent("msg2") is not null,
                MsgTimeout, "A sees its own msg1+msg2 fan-out", () => Dump(dA, dHost));
            Assert.Equal(1, obsA.PostByContent("msg1")!.Seq);
            Assert.Equal(2, obsA.PostByContent("msg2")!.Seq);
            _out.WriteLine("A posted msg1(seq1) + msg2(seq2)");

            // ===== 3. B joins, pins A from the roster, THEN backfills =====
            // Join WITHOUT auto-history so we can sequence pin-before-history: gomp
            // classifies each post once on arrival and does not retroactively upgrade
            // an already-delivered Unverified post (follow-up), and the host's roster
            // and backfill responses race on the wire. Pinning A first makes the
            // historical posts deterministically Verified.
            var obsB = new Recorder(_out, "B");
            var sessB = await gompB.JoinRoomAsync(roomAddr, obsB, requestHistory: false);

            // B must verify-once A's binding (distributed via the join roster). Poll
            // the pin directly; nudge a fresh roster if A's Hello hadn't landed yet.
            await WaitUntil(() => sessB.Directory.Knows(aAddr), LinkTimeout,
                "B pins A's binding (verify-once over the wire)",
                () => Dump(dB, dHost),
                nudge: () => sessB.RequestRosterAsync());
            _out.WriteLine("B verified + pinned A");

            // Pull history now: the missed posts arrive after the pin → Verified.
            await sessB.BackfillAsync(0);
            await WaitUntil(() => obsB.PostByContent("msg1") is not null && obsB.PostByContent("msg2") is not null,
                MsgTimeout, "B backfills msg1+msg2", () => Dump(dB, dHost));
            var b1 = obsB.PostByContent("msg1")!;
            var b2 = obsB.PostByContent("msg2")!;
            Assert.Equal(1, b1.Seq);
            Assert.Equal(2, b2.Seq);
            Assert.Equal(aAddr, b1.Sender);
            Assert.Equal(PostTrust.Verified, b1.Trust);
            Assert.Equal(PostTrust.Verified, b2.Trust);
            _out.WriteLine("B backfilled msg1+msg2 as Verified");

            // ===== 4. since-cursor backfill: after seq1 must yield ONLY seq2 =====
            var before = obsB.PostCount;
            await sessB.BackfillAsync(sinceSeq: 1);
            await WaitUntil(() => obsB.PostsSince(before).Any(p => p.Seq == 2),
                MsgTimeout, "B re-backfills seq>1", () => Dump(dB, dHost));
            var redelivered = obsB.PostsSince(before);
            Assert.All(redelivered, p => Assert.True(p.Seq > 1, $"since-cursor leaked seq {p.Seq}"));
            _out.WriteLine($"since-cursor backfill(1) re-delivered {redelivered.Length} post(s), all seq>1");

            // ===== 5. presence: A saw B arrive =====
            await WaitUntil(() => obsA.PresenceSaw(bAddr, online: true), LinkTimeout,
                "A sees B online", () => Dump(dA, dHost));
            _out.WriteLine("A observed B's presence (online)");

            // ===== 6. live fan-out to BOTH present members, Verified at B =====
            await sessA.SendChatAsync("msg3-live");
            await WaitUntil(() => obsB.PostByContent("msg3-live") is not null
                                  && obsA.PostByContent("msg3-live") is not null,
                MsgTimeout, "both members see live msg3", () => Dump(dB, dHost));
            var live = obsB.PostByContent("msg3-live")!;
            Assert.Equal(3, live.Seq);
            Assert.Equal(aAddr, live.Sender);
            Assert.Equal(PostTrust.Verified, live.Trust);
            _out.WriteLine("live msg3 fanned out to both; Verified at B");

            // ===== 6b. Invite room: the allowlist gate admits a member A adds =====
            // Wire coverage for the Invite room kind whose owner-seeding the option-B
            // identity model fixes (ticket a5fbf64b): the room owner is the host's OWN
            // base account, so an Invite allowlist is never empty and the operator
            // (== that account) is never locked out. Here A is a delegated admin
            // driving create/add — NOT a room member — so only B, once added, is
            // admitted. (Open rooms above skip the allowlist; this proves the gate.)
            var clubCreate = await gompA.CreateRoomAsync(hostBase, "club", RoomKind.Invite);
            Assert.True(clubCreate.Ok, $"create Invite room failed: {clubCreate.Error}");
            var clubAddr = clubCreate.Rooms.Single(r => r.Name == "club").Addr;
            _out.WriteLine($"Invite room 'club' = {Short(clubAddr)}");

            var addB = await gompA.AddMemberAsync(hostBase, "club", bAddr);
            Assert.True(addB.Ok, $"add member to Invite room failed: {addB.Error}");

            var obsClub = new Recorder(_out, "B@club");
            var sessClub = await gompB.JoinRoomAsync(clubAddr, obsClub);
            await WaitUntil(() => obsClub.Has(p => p.online && p.addr == bAddr) || obsClub.RosterHas(bAddr),
                LinkTimeout, "B (added) admitted to the Invite room", () => Dump(dB, dHost),
                nudge: () => sessClub.RequestRosterAsync());

            await sessClub.SendChatAsync("club-hello");
            await WaitUntil(() => obsClub.PostByContent("club-hello") is not null,
                MsgTimeout, "B's Invite-room post fans back", () => Dump(dB, dHost));
            var clubPost = obsClub.PostByContent("club-hello")!;
            Assert.Equal(bAddr, clubPost.Sender);
            Assert.Equal(1, clubPost.Seq);
            Assert.Empty(obsClub.Errors);
            _out.WriteLine("Invite room 'club': added member B admitted, posted, fan-out OK");

            // ===== 6c. management ops via the local operator door (ExecuteAdminAsync) =====
            // The frontend's management page drives these against its OWN host. The
            // e2e's member→host admin dial doesn't carry them, so exercise the live
            // handlers directly: read detail, change visibility (a sub-identity
            // re-register), edit settings, clear history, close.
            var mk = await host.ExecuteAdminAsync(new AdminRequest { CreateRoom = new CreateRoom { Name = "mgmt", Kind = RoomKind.Open } });
            Assert.True(mk.Ok, $"create mgmt failed: {mk.Error}");
            var mgmtAddr = mk.Rooms.Single().Addr;

            var det0 = await host.ExecuteAdminAsync(new AdminRequest { RoomDetail = new RoomDetail { Room = "mgmt" } });
            Assert.True(det0.Ok);
            Assert.Equal(RoomKind.Open, det0.Detail.Kind);

            // Change visibility Open -> Invite. The sub-identity re-registers; assert
            // its E-address is preserved (derived from <host>/<name>, not the ACL).
            var sk = await host.ExecuteAdminAsync(new AdminRequest { SetKind = new SetKind { Room = "mgmt", Kind = RoomKind.Invite } });
            Assert.True(sk.Ok, $"set_kind failed: {sk.Error}");
            var seam = await host.DebugRoomServiceAsync("mgmt");
            Assert.Equal(mgmtAddr, seam!.Value.Address);
            var det1 = await host.ExecuteAdminAsync(new AdminRequest { RoomDetail = new RoomDetail { Room = "mgmt" } });
            Assert.Equal(RoomKind.Invite, det1.Detail.Kind);

            var up = await host.ExecuteAdminAsync(new AdminRequest
            {
                UpdateRoom = new UpdateRoom { Room = "mgmt", DisplayName = "Mgmt Room", Topic = "ops", RetentionMax = 50 },
            });
            Assert.True(up.Ok, $"update_room failed: {up.Error}");
            var det2 = await host.ExecuteAdminAsync(new AdminRequest { RoomDetail = new RoomDetail { Room = "mgmt" } });
            Assert.Equal("Mgmt Room", det2.Detail.DisplayName);
            Assert.Equal("ops", det2.Detail.Topic);
            Assert.Equal(50, det2.Detail.RetentionMax);

            var ch = await host.ExecuteAdminAsync(new AdminRequest { ClearHistory = new ClearHistory { Room = "mgmt" } });
            Assert.True(ch.Ok, $"clear_history failed: {ch.Error}");
            var rm = await host.ExecuteAdminAsync(new AdminRequest { CloseRoom = new CloseRoom { Name = "mgmt" } });
            Assert.True(rm.Ok, $"close mgmt failed: {rm.Error}");
            _out.WriteLine("management ops OK (detail/set_kind/update/clear/close); address preserved across visibility change");

            // ===== 7. Forged: a malicious-host fan-out under A's name, bad signature =====
            await InjectAsHostAsync(host, "general", roomAddr, bAddr,
                sender: aAddr, content: "forged-words", sig: new byte[64], seq: 9001);
            await WaitUntil(() => obsB.PostByContent("forged-words") is not null,
                MsgTimeout, "B receives the forged post", () => Dump(dB, dHost));
            var forged = obsB.PostByContent("forged-words")!;
            Assert.Equal(aAddr, forged.Sender);           // claims to be A...
            Assert.Equal(PostTrust.Forged, forged.Trust);  // ...but the sig doesn't check out
            _out.WriteLine("forged post under A's name classified Forged");

            // ===== 8. Unverified: a post from a sender B has no pin for =====
            await InjectAsHostAsync(host, "general", roomAddr, bAddr,
                sender: hostBase /* never pinned by B */, content: "unverified-words", sig: new byte[64], seq: 9002);
            await WaitUntil(() => obsB.PostByContent("unverified-words") is not null,
                MsgTimeout, "B receives the unverified post", () => Dump(dB, dHost));
            Assert.Equal(PostTrust.Unverified, obsB.PostByContent("unverified-words")!.Trust);
            _out.WriteLine("post from an unknown author classified Unverified");

            // ===== 9. leave =====
            // B leaves the room. NOTE: host-side OFFLINE presence is liveness-detected
            // (rosterSweepEvery) via peerReachable, which under loopback only sees
            // OUTBOUND-dialed / Tor-control peers — an inbound room member's drop is
            // not promptly reflected (the connector can hold a stale StateConnected),
            // so connection_closed may not fire here. That is an ensemble liveness
            // concern, not gomp: gomp relays PresenceUpdate(offline) faithfully when
            // the host emits it (covered by unit tests). Observe best-effort, briefly,
            // without gating the e2e on platform liveness timing.
            await gompB.LeaveRoomAsync(roomAddr);
            var sawOffline = false;
            try
            {
                await WaitUntil(() => obsA.PresenceSaw(bAddr, online: false), TimeSpan.FromSeconds(3),
                    "A sees B offline (best-effort)");
                sawOffline = true;
            }
            catch (TimeoutException) { /* expected under loopback — see note above */ }
            _out.WriteLine(sawOffline
                ? "B left; A observed presence (offline)"
                : "B left; offline presence not surfaced in-window (host liveness under loopback — see follow-up)");

            Assert.Empty(obsA.Errors);
            Assert.Empty(obsB.Errors);
            _out.WriteLine("E2E PASS");
        }
        finally
        {
            while (disposables.Count > 0)
            {
                try { await disposables.Pop().DisposeAsync(); } catch { /* best effort */ }
            }
            try { Directory.Delete(rdv, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- helpers ----

    private static ServiceManifest MemberManifest() => ServiceManifest.NewBuilder("gomp")
        .Description("gomp rooms client").Transport(ServiceTransport.Rpc)
        .Acl(ServiceAcl.Public).MaxPayloadBytes(128 * 1024).Build();

    /// <summary>Fabricate a room fan-out from the host's room identity straight to
    /// one member — the malicious-host capability that produces Forged/Unverified
    /// posts an honest submit path never could.</summary>
    private static async Task InjectAsHostAsync(
        RoomHost host, string room, string roomAddr, string toMember,
        string sender, string content, byte[] sig, long seq)
    {
        var seam = await host.DebugRoomServiceAsync(room)
            ?? throw new InvalidOperationException($"room {room} not live");
        var body = new SignedPostBody
        {
            Sender = sender,
            Room = roomAddr,
            Content = ByteString.CopyFromUtf8(content),
            ClientTs = 1,
            Nonce = Guid.NewGuid().ToString("N"),
        }.ToByteArray();
        var env = new RoomEnvelope
        {
            Message = new RoomMessage
            {
                Seq = seq,
                HostTs = 1,
                Post = new SignedPost { Body = ByteString.CopyFrom(body), Sig = ByteString.CopyFrom(sig) },
            },
        };
        await seam.Service.SendBytesAsync(toMember, env.ToByteArray());
    }

    private async Task WaitUntil(
        Func<bool> cond, TimeSpan timeout, string desc,
        Action? onTimeout = null, Func<Task>? nudge = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextNudge = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            if (nudge is not null && DateTime.UtcNow >= nextNudge)
            {
                try { await nudge(); } catch { /* nudge is best-effort */ }
                nextNudge = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }
            await Task.Delay(50);
        }
        onTimeout?.Invoke();
        throw new TimeoutException($"timed out waiting: {desc}");
    }

    private void Dump(params DaemonProcess[] daemons)
    {
        foreach (var d in daemons)
            _out.WriteLine($"--- {d.Name} daemon log tail ---\n{d.TailLog(25)}");
    }

    private static string Short(string addr) => addr.Length <= 12 ? addr : addr[..12] + "…";

    /// <summary>Thread-safe room-observer that records everything and answers the
    /// test's wait-predicates.</summary>
    private sealed class Recorder : IRoomObserver
    {
        private readonly object _g = new();
        private readonly List<ReceivedPost> _posts = new();
        private readonly List<(string addr, bool online)> _presence = new();
        private List<RoomMemberPresence> _roster = new();
        public List<(string code, string msg)> Errors { get; } = new();

        private readonly ITestOutputHelper _out;
        private readonly string _who;
        public Recorder(ITestOutputHelper o, string who) { _out = o; _who = who; }

        public Task OnPostAsync(ReceivedPost p)
        {
            lock (_g) _posts.Add(p);
            _out.WriteLine($"[{_who}] post seq={p.Seq} from={Short(p.Sender)} trust={p.Trust} content='{Encoding.UTF8.GetString(p.Content)}'");
            return Task.CompletedTask;
        }

        public Task OnPresenceAsync(RoomMemberPresence pr)
        {
            lock (_g) _presence.Add((pr.Addr, pr.Online));
            _out.WriteLine($"[{_who}] presence {Short(pr.Addr)} online={pr.Online}");
            return Task.CompletedTask;
        }

        public Task OnRosterAsync(IReadOnlyList<RoomMemberPresence> members)
        {
            lock (_g) _roster = members.ToList();
            _out.WriteLine($"[{_who}] roster [{string.Join(", ", members.Select(m => Short(m.Addr) + (m.Online ? "+" : "-")))}]");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(string code, string message)
        {
            lock (_g) Errors.Add((code, message));
            _out.WriteLine($"[{_who}] ERROR {code}: {message}");
            return Task.CompletedTask;
        }

        public int PostCount { get { lock (_g) return _posts.Count; } }
        public ReceivedPost? PostByContent(string content)
        {
            lock (_g) return _posts.FirstOrDefault(p => Encoding.UTF8.GetString(p.Content) == content);
        }
        public ReceivedPost[] PostsSince(int index) { lock (_g) return _posts.Skip(index).ToArray(); }
        public bool Has(Func<(string addr, bool online), bool> pred) { lock (_g) return _presence.Any(pred); }
        public bool PresenceSaw(string addr, bool online) { lock (_g) return _presence.Any(e => e.addr == addr && e.online == online); }
        public bool RosterHas(string addr) { lock (_g) return _roster.Any(m => m.Addr == addr); }
    }
}
