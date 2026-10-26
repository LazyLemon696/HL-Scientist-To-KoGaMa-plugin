using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using OpenKogama.Api;

namespace HalfLife;

// Commands:
// !scientist - spawns random scientist
// !scientist [glasses / einstein / luther / slick ] - spawns scientist, depending on which variation you have selected
// !scientist remove - removes nearest scientist to you (admin only)
// !scientist clear - removes all scientists (admin only)
// !scientist follow - a scientist follows you
// !scientist heal - a scientist heals you
// !scientist hurt {amount} (or just !hurt) deals damage to you (for testing purposes)
// Made by LazyLemon using real Source Code of Half-Life, which was made by Valve. Not LazyLemon's Team nor OpenKogama are affliated to Valve corporation.

public sealed partial class ScientistPlugin : IPlugin
{

    const float ScientistHealth = 100f;
    const int MaxScientists = 10;
    static readonly int RespawnSeconds = 20;
    static readonly int RecoverSeconds = 90;

    const float WanderSpeed = 3.5f;
    const float FollowSpeed = 7f;
    const float RunSpeed = 10f;
    const float WanderMin = 4f;
    const float WanderMax = 18f;
    const float LeashRadius = 60f;

    const float NoticeRange = 13f;
    const float ThreatRange = 9f;
    const float AlarmRange = 20f;
    const float SpeechRange = 40f;
    const float UseRange = 8f;
    const float TouchRadius = 1.1f;
    const float PushSpeed = 1.2f;

    const float HealThreshold = 50f;
    const long HealCooldownMs = 60_000;
    const float HealNoticeRange = 12f;
    const float HealReach = 1.7f;
    const float HealRange = 3f;
    static readonly string? NeedleItem = "Health";
    const float HitDamageGuess = 18f;
    static readonly bool HealOnlyFollowers = false;

    const float FollowStart = 3.4f;
    const float FollowStop = 2.4f;
    const float FollowGiveUp = 45f;

    const long ProvokedMs = 90_000;
    const long FearMemoryMs = 15_000;

    const string Prefix = "<color=#8FD3FF>[Scientist]</color>: ";
    static readonly string[] Variants = ["glasses", "einstein", "luther", "slick"];

    readonly object _gate = new();
    readonly ConcurrentQueue<Action> _inbox = new();
    readonly List<Brain> _brains = new();
    readonly Dictionary<int, Track> _tracks = new();
    readonly List<Medkit> _medkits = new();
    readonly List<Later> _later = new();
    readonly List<(IWorld World, Vector3 At, string Text)> _outbox = new();
    readonly Dictionary<IWorld, List<Snap>> _snaps = new();
    readonly Dictionary<Type, PropertyInfo?> _healthProperties = new();
    readonly Dictionary<string, AvatarSkin?> _skins = new();
    IServer? _server;
    volatile bool _active;
    long _talkWaitUntil;

    public void Load(IServer server)
    {
        _server = server;
        foreach (string variant in Variants) _skins[variant] = LoadSkin(variant);
        _active = true;

        server.Update += OnUpdate;
        server.NpcDamaged += OnNpcDamaged;
        server.NpcKilled += OnNpcKilled;
        server.PlayerHit += OnPlayerHit;
        server.PlayerKilled += OnPlayerKilled;
        server.PlayerKilledByNpc += OnPlayerKilledByNpc;
        server.PlayerLeft += OnPlayerLeft;
        server.PlayerGestured += OnPlayerGestured;
        server.PlayerPickedUp += OnPlayerPickedUp;
        server.AddCommand("scientist", OnCommand);
        server.AddCommand("sci", OnCommand);
    }

    public void Unload()
    {
        IServer? server = _server;
        if (server is not null)
        {
            server.Update -= OnUpdate;
            server.NpcDamaged -= OnNpcDamaged;
            server.NpcKilled -= OnNpcKilled;
            server.PlayerHit -= OnPlayerHit;
            server.PlayerKilled -= OnPlayerKilled;
            server.PlayerKilledByNpc -= OnPlayerKilledByNpc;
            server.PlayerLeft -= OnPlayerLeft;
            server.PlayerGestured -= OnPlayerGestured;
            server.PlayerPickedUp -= OnPlayerPickedUp;
        }

        lock (_gate)
        {
            _active = false;
            foreach (Medkit kit in _medkits) RemoveObject(kit.World, kit.Id);
            foreach (Brain brain in _brains) RemoveNpc(brain.Npc);
            _medkits.Clear();
            _brains.Clear();
            _tracks.Clear();
            _later.Clear();
            _outbox.Clear();
            _snaps.Clear();
            _healthProperties.Clear();
            _skins.Clear();
            _inbox.Clear();
            _server = null;
        }
    }

    AvatarSkin? LoadSkin(string variant)
    {
        string name = $"scientist-{variant}.kgavatar";
        try
        {
            using Stream? stream = typeof(ScientistPlugin).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                _server?.Log($"scientist: {name} is not embedded in the plugin, that variant uses the default avatar");
                return null;
            }
            using var reader = new StreamReader(stream);
            return AvatarSkin.FromJson(reader.ReadToEnd());
        }
        catch (Exception error)
        {
            _server?.Log($"scientist: could not read {name}: {error.Message}");
            return null;
        }
    }

    void Enqueue(Action work)
    {
        if (_active) _inbox.Enqueue(work);
    }

    void OnUpdate(float seconds)
    {
        lock (_gate)
        {
            if (!_active) return;
            try
            {
                Step(Environment.TickCount64);
            }
            catch (Exception error)
            {
                _server?.Log($"scientist: {error}");
            }
        }
    }

    void OnCommand(IPlayer player, string[] arguments) => Enqueue(() => Command(player, arguments));

    void OnNpcDamaged(INpc npc, IPlayer? by, float amount)
    {
        int id = npc.Id;
        Enqueue(() => Damaged(id, by));
    }

    void OnNpcKilled(INpc npc, IPlayer? by)
    {
        int id = npc.Id;
        Enqueue(() => Killed(id, by));
    }

    void OnPlayerHit(IPlayer victim, IPlayer attacker) => Enqueue(() => Hit(victim));

    void OnPlayerKilled(IPlayer victim, IPlayer killer) => Enqueue(() => Died(victim));

    void OnPlayerKilledByNpc(IPlayer victim, INpc killer) => Enqueue(() => Died(victim));

    void OnPlayerLeft(IPlayer player)
    {
        int actor = player.Actor;
        Enqueue(() => Left(actor));
    }

    void OnPlayerGestured(IPlayer player, Gesture gesture) => Enqueue(() => Gestured(player, gesture));

    void OnPlayerPickedUp(IPlayer player, AvatarItem item) => Enqueue(() => PickedUp(player, item));

    void Step(long now)
    {
        _snaps.Clear();
        while (_inbox.TryDequeue(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception error)
            {
                _server?.Log($"scientist: {error}");
            }
        }

        if (_brains.Count == 0 && _medkits.Count == 0 && _later.Count == 0)
        {
            Flush();
            return;
        }

        RunLater(now);
        for (int i = _brains.Count - 1; i >= 0; i--)
        {
            Brain brain = _brains[i];
            if (!brain.Npc.Exists)
            {
                ReleaseKit(brain, now);
                _brains.RemoveAt(i);
                continue;
            }

            try
            {
                Think(brain, now);
            }
            catch (Exception error)
            {
                _server?.Log($"scientist: {error}");
            }

            if (brain.Remove)
            {
                ReleaseKit(brain, now);
                RemoveNpc(brain.Npc);
                _brains.RemoveAt(i);
            }
        }

        UpdateMedkits(now);
        Flush();
    }

    void RunLater(long now)
    {
        for (int i = _later.Count - 1; i >= 0; i--)
        {
            if (i >= _later.Count || _later[i].At > now) continue;
            Action run = _later[i].Run;
            _later.RemoveAt(i);
            try
            {
                run();
            }
            catch (Exception error)
            {
                _server?.Log($"scientist: {error}");
            }
        }
    }

    bool Speak(Brain brain, Say say, string? player, bool urgent = false, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force)
        {
            bool blocked = urgent ? now < brain.UrgentGap : (now < _talkWaitUntil || now < brain.NextSpeak);
            if (blocked) return false;
        }
        brain.NextSpeak = now + 2500;
        brain.UrgentGap = now + 1200;
        brain.SpokeAt = now;
        _talkWaitUntil = now + 1500;
        _outbox.Add((brain.World, brain.Npc.Position, Lines.Pick(say, Clean(player ?? "Doctor"))));
        return true;
    }

    void Flush()
    {
        if (_outbox.Count == 0) return;
        foreach ((IWorld world, Vector3 at, string text) in _outbox)
        {
            string line = Prefix + text;
            foreach (IPlayer player in world.Players)
                if (player.Position is Vector3 there && Vector3.Distance(there, at) <= SpeechRange)
                    player.Message(line);
        }
        _outbox.Clear();
    }

    static string Clean(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (c != '<' && c != '>' && !char.IsControl(c)) text.Append(c);
        string cleaned = text.ToString().Trim();
        if (cleaned.Length > 24) cleaned = cleaned[..24];
        return cleaned.Length == 0 ? "Doctor" : cleaned;
    }

    void Command(IPlayer player, string[] arguments)
    {
        string sub = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "spawn";
        switch (sub)
        {
            case "spawn":
                Spawn(player, arguments.Length > 1 ? arguments[1] : null);
                break;
            case "glasses" or "einstein" or "luther" or "slick" or "random":
                Spawn(player, sub);
                break;
            case "remove":
                Remove(player);
                break;
            case "clear":
                Clear(player);
                break;
            case "follow":
                FollowCommand(player);
                break;
            case "heal":
                HealCommand(player);
                break;
            case "hurt":
                Hurt(player, arguments.Length > 1 ? arguments[1] : null);
                break;
            default:
                Help(player);
                break;
        }
    }

    static void Help(IPlayer player)
    {
        player.Message("!scientist [glasses|einstein|luther|slick]  spawns a scientist next to you");
        player.Message("!scientist remove  removes the nearest one, !scientist clear removes all (admins)");
        player.Message("!scientist follow  and  !scientist heal  do what waving and nodding at him do");
        player.Message("Wave at him: he follows you or waits. Nod: he treats you. Shake your head: he stays. !scientist hurt [amount] hurts you, to test healing.");
    }

    void Spawn(IPlayer player, string? requested)
    {
        int count = 0;
        foreach (Brain brain in _brains)
            if (brain.Npc.Exists) count++;
        if (count >= MaxScientists)
        {
            player.Message($"There are already {count} scientists, remove some with !scientist remove");
            return;
        }
        if (player.Position is not Vector3 at)
        {
            player.Message("Move a bit first, then try again");
            return;
        }

        string variant = requested is not null && Array.IndexOf(Variants, requested.ToLowerInvariant()) >= 0
            ? requested.ToLowerInvariant()
            : Variants[Random.Shared.Next(Variants.Length)];

        IWorld world = player.World;
        Vector3 spot = SpawnSpot(world, at);
        INpc npc = world.SpawnNpc("Scientist", spot, YawTo(spot, at), _skins.GetValueOrDefault(variant), 1f, 1);
        npc.MaxHealth = ScientistHealth;
        npc.Speed = WanderSpeed;

        long now = Environment.TickCount64;
        var created = new Brain(npc, world, variant, spot);
        created.NextAction = now + 2500;
        created.NextChat = now + Between(20_000, 45_000);
        Enter(created, Mode.Idle, now);
        _brains.Add(created);
        player.Message($"A scientist ({variant}) is here. Wave to make him follow you, nod if you need treatment. !scientist for more");
    }

    static Vector3 SpawnSpot(IWorld world, Vector3 at)
    {
        float start = Random.Shared.NextSingle() * MathF.Tau;
        for (int i = 0; i < 8; i++)
        {
            float angle = start + i * MathF.PI / 4f;
            Vector3 spot = at + new Vector3(MathF.Sin(angle) * 2.2f, 0.3f, MathF.Cos(angle) * 2.2f);
            if (world.LineOfSight(at + Vector3.UnitY, spot + Vector3.UnitY)) return spot;
        }
        return at + new Vector3(0f, 0.5f, 0f);
    }

    void Remove(IPlayer player)
    {
        Brain? nearest = player.Position is Vector3 at ? NearestBrain(player.World, at, 20f) : null;
        if (nearest is null)
        {
            player.Message("No scientist within 20 blocks");
            return;
        }
        ReleaseKit(nearest, Environment.TickCount64);
        RemoveNpc(nearest.Npc);
        _brains.Remove(nearest);
        player.Message("Scientist removed");
    }

    void Clear(IPlayer player)
    {
        if (!player.IsAdmin)
        {
            player.Message("Only the server's admins can remove every scientist");
            return;
        }
        long now = Environment.TickCount64;
        int count = _brains.Count;
        foreach (Brain brain in _brains)
        {
            ReleaseKit(brain, now);
            RemoveNpc(brain.Npc);
        }
        _brains.Clear();
        player.Message($"{count} scientists removed");
    }

    void FollowCommand(IPlayer player) => Gestured(player, Gesture.Wave);

    void HealCommand(IPlayer player) => Gestured(player, Gesture.Nod);

    void Hurt(IPlayer player, string? text)
    {
        float amount = text is not null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 60f;
        player.Damage(amount);
        Track track = TrackOf(player.Actor);
        track.Hp = MathF.Max(1f, track.Hp - amount);
        player.Message($"You took {amount:0} damage, now stand near a scientist or nod at him");
    }

    void Damaged(int id, IPlayer? by)
    {
        Brain? brain = BrainOf(id);
        if (brain is null || !brain.Npc.Alive) return;

        long now = Environment.TickCount64;
        brain.HurtSince = 0;
        Vector3 pos = brain.Npc.Position;

        if (by is null)
        {
            if (now - brain.LastPain > 1500)
            {
                brain.LastPain = now;
                Speak(brain, Say.Pain, null, urgent: true);
            }
            return;
        }

        brain.Provoked[by.Actor] = now + ProvokedMs;
        brain.FearActor = by.Actor;
        brain.LastEnemySeen = now;
        Vector3 threat = by.Position ?? pos + new Vector3(1f, 0f, 0f);
        brain.LastFearPos = threat;

        if (now - brain.LastFear > 6000)
        {
            brain.LastFear = now;
            brain.LastPain = now;
            Speak(brain, Say.Fear, by.Name, urgent: true);
        }
        else if (now - brain.LastPain > 1500)
        {
            brain.LastPain = now;
            Speak(brain, Say.Pain, by.Name, urgent: true);
        }

        bool alreadyRunning = (brain.Mode is Mode.Flee or Mode.Panic) && now - brain.LastFleeAt < 2500;
        if (!alreadyRunning) Flee(brain, threat, now);

        foreach (Brain friend in _brains)
        {
            if (ReferenceEquals(friend, brain) || !ReferenceEquals(friend.World, brain.World) || !friend.Npc.Exists || !friend.Npc.Alive) continue;
            Vector3 there = friend.Npc.Position;
            if (Vector3.Distance(there, pos) > AlarmRange) continue;
            friend.Provoked[by.Actor] = now + ProvokedMs / 3;
            friend.LastFearPos = threat;
            if (friend.Mode is Mode.Idle or Mode.Wander) friend.Npc.Face(YawTo(there, threat));
            if (Random.Shared.Next(100) < 70) Speak(friend, Say.Stop, by.Name);
        }
    }

    void Killed(int id, IPlayer? by)
    {
        Brain? brain = BrainOf(id);
        if (brain is null) return;
        long now = Environment.TickCount64;
        if (!brain.Dead) MarkDead(brain, now);
        if (by is not null) brain.Provoked[by.Actor] = now + ProvokedMs;
        Speak(brain, Say.Death, by?.Name, force: true);
    }

    void Hit(IPlayer victim)
    {
        Track track = TrackOf(victim.Actor);
        track.Hp = MathF.Max(1f, track.Hp - HitDamageGuess);
        if (victim.Position is Vector3 at) Alarm(victim.World, at, Environment.TickCount64);
    }

    void Died(IPlayer victim)
    {
        TrackOf(victim.Actor).Hp = 100f;
        if (victim.Position is Vector3 at) Alarm(victim.World, at, Environment.TickCount64);
    }

    void Left(int actor)
    {
        long now = Environment.TickCount64;
        _tracks.Remove(actor);
        foreach (Brain brain in _brains)
        {
            brain.Provoked.Remove(actor);
            brain.Greeted.Remove(actor);
            brain.Cured.Remove(actor);
            brain.Los.Remove(actor);
            if (brain.FearActor == actor) brain.FearActor = -1;
            if (brain.FollowActor == actor) StopFollowing(brain, now, speak: false);
            if (brain.HealActor == actor && brain.Mode == Mode.Heal) EndHeal(brain, now, false, 1000);
        }
    }

    void PickedUp(IPlayer player, AvatarItem item)
    {
        if (item != AvatarItem.Health) return;
        TrackOf(player.Actor).Hp = 100f;
        foreach (Medkit kit in _medkits)
            if (kit.Actor == player.Actor) kit.Taken = true;
    }

    void Gestured(IPlayer player, Gesture gesture)
    {
        if (player.Position is not Vector3 at) return;
        Brain? brain = NearestBrain(player.World, at, UseRange);
        if (brain is null)
        {
            if (gesture == Gesture.Wave) player.Message("There is no scientist within reach, !scientist spawns one");
            return;
        }

        long now = Environment.TickCount64;
        if (IsProvoked(brain, player.Actor, now) || brain.Mode is Mode.Flee or Mode.Panic or Mode.Hide or Mode.Startle)
        {
            Speak(brain, Say.Refuse, player.Name, urgent: true);
            return;
        }

        switch (gesture)
        {
            case Gesture.Wave:
                if (brain.FollowActor == player.Actor) StopFollowing(brain, now, speak: true);
                else StartFollowing(brain, player, now);
                break;
            case Gesture.HeadShake:
                if (brain.FollowActor == player.Actor) StopFollowing(brain, now, speak: true);
                break;
            case Gesture.Nod:
                RequestHeal(brain, player, now);
                break;
        }
    }

    static long Between(int min, int max) => Random.Shared.Next(min, max);

    static float Dist2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    static float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, to.Z - from.Z) * 180f / MathF.PI;

    static Vector3 Flat(Vector3 value) => new(value.X, 0f, value.Z);

    static bool IsProvoked(Brain brain, int actor, long now) => brain.Provoked.TryGetValue(actor, out long until) && until > now;

    static void SetHolding(INpc npc, string? item)
    {
        try
        {
            npc.Holding = item;
        }
        catch (ArgumentException)
        {
        }
    }

    static void RemoveNpc(INpc npc)
    {
        try
        {
            npc.Remove();
        }
        catch (Exception)
        {
        }
    }

    static void RemoveObject(IWorld world, int id)
    {
        try
        {
            world.Remove(id);
        }
        catch (Exception)
        {
        }
    }

    Brain? BrainOf(int id)
    {
        foreach (Brain brain in _brains)
            if (brain.Npc.Id == id) return brain;
        return null;
    }

    Brain? NearestBrain(IWorld world, Vector3 at, float range)
    {
        Brain? best = null;
        float bestDistance = range;
        foreach (Brain brain in _brains)
        {
            if (!ReferenceEquals(brain.World, world) || !brain.Npc.Exists || brain.Dead) continue;
            float distance = Vector3.Distance(brain.Npc.Position, at);
            if (distance > bestDistance) continue;
            best = brain;
            bestDistance = distance;
        }
        return best;
    }

    static Snap? NearestSnap(List<Snap> snaps, Vector3 pos, float range)
    {
        Snap? best = null;
        float bestDistance = range;
        foreach (Snap snap in snaps)
        {
            if (!snap.Alive) continue;
            float distance = Dist2D(snap.Pos, pos);
            if (distance > bestDistance) continue;
            best = snap;
            bestDistance = distance;
        }
        return best;
    }

    static Snap? FindSnap(List<Snap> snaps, int actor)
    {
        foreach (Snap snap in snaps)
            if (snap.Actor == actor) return snap;
        return null;
    }

    Track TrackOf(int actor)
    {
        if (!_tracks.TryGetValue(actor, out Track? track)) _tracks[actor] = track = new Track();
        return track;
    }

    List<Snap> SnapsFor(IWorld world, long now)
    {
        if (_snaps.TryGetValue(world, out List<Snap>? cached)) return cached;
        var list = new List<Snap>();
        foreach (IPlayer player in world.Players)
        {
            if (player.Position is not Vector3 pos) continue;
            Track track = TrackOf(player.Actor);
            if (track.At == 0)
            {
                track.Last = pos;
                track.At = now;
            }
            else if (now - track.At >= 120)
            {
                float speed = Dist2D(pos, track.Last) / ((now - track.At) / 1000f);
                track.Speed = track.Speed * 0.5f + speed * 0.5f;
                track.Last = pos;
                track.At = now;
            }
            float? real = RealHealth(player);
            list.Add(new Snap(player, player.Actor, pos, player.Alive, track.Speed, real ?? track.Hp, real.HasValue));
        }
        _snaps[world] = list;
        return list;
    }

    float? RealHealth(IPlayer player)
    {
        Type type = player.GetType();
        if (!_healthProperties.TryGetValue(type, out PropertyInfo? property))
        {
            property = type.GetProperty("Health", BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && property.GetIndexParameters().Length != 0) property = null;
            _healthProperties[type] = property;
        }
        if (property is null) return null;

        try
        {
            object? raw = property.GetValue(player);
            if (raw is float single) return single;
            if (raw is double number) return (float)number;
            if (raw is int whole) return whole;
        }
        catch (Exception)
        {
        }
        return null;
    }

    bool CanSee(Brain brain, Snap snap, long now)
    {
        if (brain.Los.TryGetValue(snap.Actor, out (long At, bool Seen) cached) && now - cached.At < 300) return cached.Seen;
        bool seen = brain.World.LineOfSight(brain.Npc.EyePosition, snap.Pos + new Vector3(0f, 1.2f, 0f));
        brain.Los[snap.Actor] = (now, seen);
        return seen;
    }

    enum Mode { Idle, Wander, Follow, Flee, Hide, Panic, Startle, Heal, Dead }

    enum Stage { Approach, Prep, Needle, Wait }

    sealed record Snap(IPlayer Player, int Actor, Vector3 Pos, bool Alive, float Speed, float Hp, bool HpKnown);

    sealed record Later(long At, Action Run);

    sealed class Track
    {
        public float Hp = 100f;
        public Vector3 Last;
        public long At;
        public float Speed;
    }

    sealed class Medkit(IWorld world, int id, int actor, long expires)
    {
        public IWorld World => world;
        public int Id => id;
        public int Actor => actor;
        public long ExpiresAt { get; set; } = expires;
        public bool Taken { get; set; }
    }

    sealed class Brain(INpc npc, IWorld world, string variant, Vector3 home)
    {
        public INpc Npc => npc;
        public IWorld World => world;
        public string Variant => variant;
        public Vector3 Home => home;

        public Mode Mode;
        public long Since;
        public long Until = long.MaxValue;
        public Vector3 Goal;
        public Vector3 LastFearPos;
        public Vector3 PushFrom;

        public int FollowActor = -1;
        public int FearActor = -1;
        public int HealActor = -1;
        public Stage Stage;
        public Medkit? Kit;
        public int HealFails;

        public long NextAction, NextThreat, NextSense, NextRepath, NextScream, NextDash, NextStartle, NextChat, NextStare, NextStuck;
        public long NextSpeak, UrgentGap, SpokeAt, LastFear, LastPain, LastPush, TouchUntil, StareSince;
        public long LastEnemySeen, LastFleeAt, HealReadyAt, StepAt, DiedAt, ReviveAt, HurtSince;

        public Vector3 LastPos;
        public int Stuck, StuckFlee;

        public bool PendingStep, Dead, Reviving, Remove, SaidWound, SaidMortal;

        public readonly Dictionary<int, long> Provoked = new();
        public readonly HashSet<int> Greeted = new();
        public readonly Dictionary<int, int> Cured = new();
        public readonly Dictionary<int, (long At, bool Seen)> Los = new();
    }
}
