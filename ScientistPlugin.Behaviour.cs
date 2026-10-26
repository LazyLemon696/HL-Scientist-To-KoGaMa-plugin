using System.Numerics;
using OpenKogama.Api;

namespace HalfLife;

public sealed partial class ScientistPlugin
{
    void Enter(Brain brain, Mode mode, long now, long forMs = 0)
    {
        brain.Mode = mode;
        brain.Since = now;
        brain.Until = forMs > 0 ? now + forMs : long.MaxValue;
        brain.Stuck = 0;
        brain.LastPos = brain.Npc.Position;
        brain.NextStuck = now + 500;
        brain.NextRepath = 0;
        if (mode != Mode.Idle) brain.PendingStep = false;
    }

    void Think(Brain brain, long now)
    {
        INpc npc = brain.Npc;
        if (!npc.Alive)
        {
            ThinkDead(brain, now);
            return;
        }
        if (brain.Dead) Revive(brain, now);

        Vector3 pos = npc.Position;
        List<Snap> snaps = SnapsFor(brain.World, now);
        ExpireProvoked(brain, now);

        if (Threatened(brain, snaps, pos, now)) return;

        switch (brain.Mode)
        {
            case Mode.Idle:
                ThinkIdle(brain, snaps, pos, now);
                break;
            case Mode.Wander:
                ThinkWander(brain, pos, now);
                break;
            case Mode.Follow:
                ThinkFollow(brain, snaps, pos, now);
                break;
            case Mode.Flee:
                ThinkFlee(brain, pos, now);
                break;
            case Mode.Hide:
                ThinkHide(brain, pos, now);
                break;
            case Mode.Panic:
                ThinkPanic(brain, pos, now);
                break;
            case Mode.Startle:
                if (now >= brain.Until)
                {
                    Enter(brain, Mode.Idle, now);
                    brain.NextAction = now + 800;
                }
                break;
            case Mode.Heal:
                ThinkHeal(brain, snaps, pos, now);
                break;
        }

        if (brain.Mode is Mode.Idle or Mode.Wander or Mode.Follow) Social(brain, snaps, pos, now);
        Wellbeing(brain, now);
    }

    void ThinkDead(Brain brain, long now)
    {
        if (!brain.Dead) MarkDead(brain, now);

        if (RespawnSeconds <= 0)
        {
            if (now - brain.DiedAt > 4000) brain.Remove = true;
            return;
        }

        if (!brain.Reviving)
        {
            if (now - brain.DiedAt < RespawnSeconds * 1000L) return;
            brain.Npc.Respawn(brain.Home);
            brain.Reviving = true;
            brain.ReviveAt = now;
        }
        else if (now - brain.ReviveAt > 4000)
        {
            brain.Npc.Respawn(brain.Home);
            brain.ReviveAt = now;
        }
    }

    void MarkDead(Brain brain, long now)
    {
        brain.Dead = true;
        brain.DiedAt = now;
        brain.Reviving = false;
        brain.FollowActor = -1;
        brain.HealActor = -1;
        ReleaseKit(brain, now);
        SetHolding(brain.Npc, null);
        brain.Mode = Mode.Dead;
    }

    void Revive(Brain brain, long now)
    {
        brain.Dead = false;
        brain.Reviving = false;
        brain.Provoked.Clear();
        brain.Greeted.Clear();
        brain.Cured.Clear();
        brain.Los.Clear();
        brain.FearActor = -1;
        brain.SaidWound = false;
        brain.SaidMortal = false;
        brain.HealReadyAt = 0;
        brain.HealFails = 0;
        brain.HurtSince = 0;
        brain.Npc.Speed = WanderSpeed;
        brain.Npc.Stop();
        Enter(brain, Mode.Idle, now);
        brain.NextAction = now + 2000;
    }

    static void ExpireProvoked(Brain brain, long now)
    {
        if (brain.Provoked.Count == 0) return;
        List<int>? expired = null;
        foreach (KeyValuePair<int, long> pair in brain.Provoked)
            if (pair.Value <= now) (expired ??= new List<int>()).Add(pair.Key);
        if (expired is null) return;
        foreach (int actor in expired) brain.Provoked.Remove(actor);
    }

    bool Threatened(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        if ((brain.Mode is Mode.Flee or Mode.Panic) || now < brain.NextThreat) return false;
        brain.NextThreat = now + 250;

        foreach (Snap snap in snaps)
        {
            if (!snap.Alive || !IsProvoked(brain, snap.Actor, now)) continue;
            float distance = Dist2D(pos, snap.Pos);
            if (distance > NoticeRange * 1.2f || !CanSee(brain, snap, now)) continue;

            brain.LastEnemySeen = now;
            brain.FearActor = snap.Actor;
            brain.LastFearPos = snap.Pos;
            if (distance > ThreatRange || now - brain.LastFleeAt < 2500) continue;

            if (now - brain.LastFear > 6000)
            {
                brain.LastFear = now;
                Speak(brain, Say.Fear, snap.Player.Name, urgent: true);
            }
            Flee(brain, snap.Pos, now);
            return true;
        }
        return false;
    }

    void Flee(Brain brain, Vector3 threat, long now)
    {
        if (brain.Mode == Mode.Heal) EndHeal(brain, now, false, 8000);
        brain.FollowActor = -1;
        brain.LastFleeAt = now;
        brain.StuckFlee = 0;
        brain.NextScream = now + Between(200, 900);

        INpc npc = brain.Npc;
        Vector3 pos = npc.Position;
        Vector3? cover = FindCover(brain, pos, threat);
        if (cover is Vector3 goal)
        {
            brain.Goal = goal;
            npc.Speed = RunSpeed;
            npc.WalkTo(goal);
            Enter(brain, Mode.Flee, now, (long)((Dist2D(pos, goal) / RunSpeed + 3f) * 1000f));
        }
        else
        {
            Panic(brain, now);
        }
    }

    void Panic(Brain brain, long now)
    {
        brain.Npc.Stop();
        brain.NextDash = 0;
        Enter(brain, Mode.Panic, now, Between(3000, 5000));
    }

    Vector3? FindCover(Brain brain, Vector3 pos, Vector3 threat)
    {
        Vector3 away = Flat(pos - threat);
        float baseAngle = away.LengthSquared() > 0.01f ? MathF.Atan2(away.X, away.Z) : Random.Shared.NextSingle() * MathF.Tau;
        Vector3 eye = brain.Npc.EyePosition;
        Vector3 chest = new(0f, 1.1f, 0f);
        Vector3 threatEye = threat + new Vector3(0f, 1.5f, 0f);

        Vector3 best = default;
        float bestScore = float.MinValue;
        bool found = false;
        for (int i = 0; i < 14; i++)
        {
            float spread = (i % 7 - 3) * 0.45f;
            float angle = baseAngle + spread + (Random.Shared.NextSingle() - 0.5f) * 0.3f;
            float distance = 10f + Random.Shared.NextSingle() * 16f;
            Vector3 goal = pos + new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance);
            if (Dist2D(goal, brain.Home) > LeashRadius + 15f) continue;
            if (!brain.World.LineOfSight(eye, goal + chest)) continue;

            bool hidden = !brain.World.LineOfSight(threatEye, goal + chest);
            float score = distance + Dist2D(goal, threat) * 0.6f + (hidden ? 30f : 0f) - MathF.Abs(spread) * 6f;
            if (score <= bestScore) continue;
            best = goal;
            bestScore = score;
            found = true;
        }
        if (!found) return null;
        return best;
    }

    void ThinkFlee(Brain brain, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;
        if (now >= brain.NextScream)
        {
            Speak(brain, Say.Scream, null, urgent: true);
            brain.NextScream = now + Between(2200, 4500);
        }

        bool arrived = !npc.Walking || Dist2D(pos, brain.Goal) < 1.5f;
        if (arrived || now >= brain.Until)
        {
            npc.Stop();
            Enter(brain, Mode.Hide, now, Between(4000, 9000));
            return;
        }

        if (!Stuck(brain, pos, now)) return;
        brain.StuckFlee++;
        npc.Stop();
        if (brain.StuckFlee < 2 && FindCover(brain, pos, brain.LastFearPos) is Vector3 detour)
        {
            brain.Goal = detour;
            npc.WalkTo(detour);
            brain.Until = now + 8000;
        }
        else
        {
            Panic(brain, now);
        }
    }

    void ThinkHide(Brain brain, Vector3 pos, long now)
    {
        brain.Npc.Face(YawTo(pos, brain.LastFearPos));
        if (now < brain.Until) return;

        if (now - brain.LastEnemySeen < FearMemoryMs)
        {
            brain.Until = now + 2000;
            return;
        }
        brain.FearActor = -1;
        Enter(brain, Mode.Idle, now);
        brain.NextAction = now + Between(1500, 3500);
    }

    void ThinkPanic(Brain brain, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;
        if (now >= brain.NextScream)
        {
            Speak(brain, Say.Scream, null, urgent: true);
            brain.NextScream = now + Between(1800, 3500);
        }
        if (now >= brain.Until)
        {
            npc.Stop();
            Enter(brain, Mode.Hide, now, Between(3000, 6000));
            return;
        }
        if (now < brain.NextDash && npc.Walking) return;

        float angle = Random.Shared.NextSingle() * MathF.Tau;
        float distance = 4f + Random.Shared.NextSingle() * 4f;
        npc.Speed = RunSpeed;
        npc.WalkTo(pos + new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance));
        if (Random.Shared.Next(100) < 35) npc.Jump(0.3f);
        brain.NextDash = now + Between(700, 1400);
    }

    void Alarm(IWorld world, Vector3 at, long now)
    {
        foreach (Brain brain in _brains)
        {
            if (!ReferenceEquals(brain.World, world) || brain.Dead || !brain.Npc.Exists || !brain.Npc.Alive) continue;
            if (brain.Mode is not (Mode.Idle or Mode.Wander or Mode.Follow)) continue;
            Vector3 pos = brain.Npc.Position;
            if (Vector3.Distance(pos, at) > AlarmRange || now < brain.NextStartle) continue;

            brain.NextStartle = now + 3000;
            brain.LastFearPos = at;
            if (Random.Shared.Next(100) < 30) Speak(brain, Say.Startle, null, urgent: true);
            if (brain.Mode == Mode.Follow) continue;

            brain.Npc.Stop();
            brain.Npc.Face(YawTo(pos, at));
            brain.Npc.Jump(0.18f);
            Enter(brain, Mode.Startle, now, Between(900, 1800));
        }
    }

    void ThinkIdle(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        if (brain.PendingStep)
        {
            if (now < brain.StepAt) return;
            brain.PendingStep = false;
            StepAside(brain, pos, now);
            return;
        }
        if (now < brain.TouchUntil || now < brain.NextAction) return;

        int roll = Random.Shared.Next(100);
        if (roll < 34)
        {
            StartWander(brain, pos, now, WanderMin, WanderMax);
        }
        else if (roll < 50)
        {
            StartWander(brain, pos, now, 1.5f, 3.5f);
        }
        else if (roll < 78)
        {
            brain.Npc.Face(brain.Npc.Yaw + Random.Shared.Next(-130, 131));
            brain.NextAction = now + Between(1500, 4000);
        }
        else if (roll < 90 && NearestSnap(snaps, pos, NoticeRange) is Snap watched)
        {
            brain.Npc.Face(YawTo(pos, watched.Pos));
            brain.NextAction = now + Between(2500, 5000);
        }
        else
        {
            brain.NextAction = now + Between(2500, 6000);
        }
    }

    void StartWander(Brain brain, Vector3 pos, long now, float min, float max)
    {
        Vector3 eye = brain.Npc.EyePosition;
        Vector3 chest = new(0f, 1f, 0f);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float distance = min + Random.Shared.NextSingle() * (max - min);
            Vector3 goal = pos + new Vector3(MathF.Sin(angle) * distance, 0f, MathF.Cos(angle) * distance);
            if (Dist2D(goal, brain.Home) > LeashRadius)
            {
                Vector3 back = Flat(brain.Home - pos);
                if (back.LengthSquared() < 0.01f) continue;
                goal = pos + Vector3.Normalize(back) * distance;
            }
            if (!brain.World.LineOfSight(eye, goal + chest)) continue;

            brain.Goal = goal;
            brain.Npc.Speed = WanderSpeed;
            brain.Npc.WalkTo(goal);
            Enter(brain, Mode.Wander, now, (long)((distance / WanderSpeed + 4f) * 1000f));
            return;
        }
        brain.NextAction = now + Between(1500, 3000);
    }

    void ThinkWander(Brain brain, Vector3 pos, long now)
    {
        if (brain.Npc.Walking && now < brain.Until && !Stuck(brain, pos, now)) return;
        brain.Npc.Stop();
        Enter(brain, Mode.Idle, now);
        brain.NextAction = now + Between(1200, 3500);
    }

    void StepAside(Brain brain, Vector3 pos, long now)
    {
        Vector3 away = Flat(pos - brain.PushFrom);
        Vector3 direction = away.LengthSquared() > 0.01f ? Vector3.Normalize(away) : Vector3.UnitX;
        Vector3 side = new(-direction.Z, 0f, direction.X);
        if (Random.Shared.Next(2) == 0) side = -side;

        Vector3 eye = brain.Npc.EyePosition;
        Vector3 chest = new(0f, 1f, 0f);
        for (int i = 0; i < 4; i++)
        {
            Vector3 goal = i switch
            {
                0 => pos + direction * 2.5f + side * 1.2f,
                1 => pos + direction * 2.5f - side * 1.2f,
                2 => pos + side * 2.5f,
                _ => pos - side * 2.5f,
            };
            if (!brain.World.LineOfSight(eye, goal + chest)) continue;

            brain.Goal = goal;
            brain.Npc.Speed = WanderSpeed + 1f;
            brain.Npc.WalkTo(goal);
            Enter(brain, Mode.Wander, now, 3000);
            return;
        }
        brain.NextAction = now + 1000;
    }

    static bool Stuck(Brain brain, Vector3 pos, long now)
    {
        if (!brain.Npc.Walking)
        {
            brain.Stuck = 0;
            brain.LastPos = pos;
            brain.NextStuck = now + 500;
            return false;
        }
        if (now < brain.NextStuck) return false;

        brain.NextStuck = now + 500;
        bool moved = Dist2D(pos, brain.LastPos) >= 0.15f;
        brain.LastPos = pos;
        if (moved)
        {
            brain.Stuck = 0;
            return false;
        }
        brain.Stuck++;
        if (brain.Stuck == 2) brain.Npc.Jump(0.5f);
        return brain.Stuck >= 5;
    }

    void StartFollowing(Brain brain, IPlayer player, long now)
    {
        brain.FollowActor = player.Actor;
        Speak(brain, Say.Follow, player.Name, urgent: true);
        if (brain.Mode is not (Mode.Idle or Mode.Wander)) return;
        brain.Npc.Stop();
        Enter(brain, Mode.Follow, now);
    }

    void StopFollowing(Brain brain, long now, bool speak)
    {
        bool was = brain.FollowActor >= 0;
        brain.FollowActor = -1;
        if (brain.Mode == Mode.Follow)
        {
            brain.Npc.Stop();
            Enter(brain, Mode.Idle, now);
            brain.NextAction = now + 1500;
        }
        if (was && speak) Speak(brain, Say.Wait, null, urgent: true);
    }

    void ThinkFollow(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;
        Snap? leader = FindSnap(snaps, brain.FollowActor);
        if (leader is null || !leader.Alive)
        {
            StopFollowing(brain, now, speak: false);
            return;
        }

        float distance = Dist2D(pos, leader.Pos);
        if (distance > FollowGiveUp)
        {
            StopFollowing(brain, now, speak: false);
            Speak(brain, Say.Lost, leader.Player.Name, urgent: true);
            return;
        }

        if (distance > FollowStart || (npc.Walking && distance > FollowStop))
        {
            if (now >= brain.NextRepath)
            {
                Vector3 toLeader = Flat(leader.Pos - pos);
                Vector3 direction = toLeader.LengthSquared() > 0.01f ? Vector3.Normalize(toLeader) : Vector3.UnitX;
                npc.Speed = distance > 12f ? RunSpeed * 0.9f : FollowSpeed;
                npc.WalkTo(leader.Pos - direction * (FollowStop - 0.5f));
                brain.NextRepath = now + 300;
            }
            if (Stuck(brain, pos, now))
            {
                npc.Jump(0.5f);
                brain.Stuck = 0;
            }
        }
        else
        {
            if (npc.Walking) npc.Stop();
            npc.Face(YawTo(pos, leader.Pos));
        }
    }

    bool CheckHurt(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        Snap? worst = null;
        foreach (Snap snap in snaps)
        {
            if (!snap.Alive || IsProvoked(brain, snap.Actor, now)) continue;
            if (snap.Hp > 60f)
            {
                brain.Cured.Remove(snap.Actor);
                continue;
            }
            if (snap.Hp > HealThreshold) continue;
            if (HealOnlyFollowers && brain.FollowActor != snap.Actor) continue;
            if (Dist2D(snap.Pos, pos) > HealNoticeRange || !CanSee(brain, snap, now)) continue;
            if (worst is null || snap.Hp < worst.Hp) worst = snap;
        }
        if (worst is null) return false;

        CureTalk(brain, worst);
        if (now < brain.HealReadyAt) return false;
        StartHeal(brain, worst, now);
        return true;
    }

    void CureTalk(Brain brain, Snap patient)
    {
        brain.Cured.TryGetValue(patient.Actor, out int said);
        Say say;
        int bit;
        if (patient.Hp <= 12.5f)
        {
            say = Say.CureC;
            bit = 4;
        }
        else if (patient.Hp <= 25f)
        {
            say = Say.CureB;
            bit = 2;
        }
        else
        {
            say = Say.CureA;
            bit = 1;
        }
        if ((said & bit) != 0) return;
        if (Speak(brain, say, patient.Player.Name)) brain.Cured[patient.Actor] = said | bit;
    }

    void RequestHeal(Brain brain, IPlayer player, long now)
    {
        Snap? patient = FindSnap(SnapsFor(brain.World, now), player.Actor);
        if (patient is null || brain.Mode == Mode.Heal) return;

        if (patient.HpKnown && patient.Hp > HealThreshold)
        {
            Speak(brain, Say.Fine, player.Name, urgent: true);
            return;
        }
        if (now < brain.HealReadyAt)
        {
            Speak(brain, Say.Later, player.Name, urgent: true);
            return;
        }
        StartHeal(brain, patient, now);
    }

    void StartHeal(Brain brain, Snap patient, long now)
    {
        brain.HealActor = patient.Actor;
        brain.Stage = Stage.Approach;
        brain.StepAt = now;
        brain.Npc.Stop();
        Enter(brain, Mode.Heal, now, 14_000);
    }

    void ThinkHeal(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;
        Snap? patient = FindSnap(snaps, brain.HealActor);
        if (patient is null || !patient.Alive || IsProvoked(brain, patient.Actor, now) || now >= brain.Until)
        {
            EndHeal(brain, now, false, 8000);
            return;
        }

        float distance = Dist2D(pos, patient.Pos);
        switch (brain.Stage)
        {
            case Stage.Approach:
                if (distance > HealNoticeRange + 6f)
                {
                    EndHeal(brain, now, false, 8000);
                    return;
                }
                if (distance > HealReach)
                {
                    if (now >= brain.NextRepath)
                    {
                        npc.Speed = RunSpeed * 0.8f;
                        npc.WalkTo(patient.Pos);
                        brain.NextRepath = now + 250;
                    }
                    if (Stuck(brain, pos, now)) npc.Jump(0.5f);
                    return;
                }
                npc.Stop();
                npc.Face(YawTo(pos, patient.Pos));
                if (!Speak(brain, Say.Heal, patient.Player.Name, urgent: true) && now - brain.StepAt < 2500) return;
                brain.Stage = Stage.Prep;
                brain.StepAt = now;
                break;

            case Stage.Prep:
                npc.Face(YawTo(pos, patient.Pos));
                if (now - brain.StepAt < 700) return;
                SetHolding(npc, NeedleItem);
                brain.Stage = Stage.Needle;
                brain.StepAt = now;
                break;

            case Stage.Needle:
                npc.Face(YawTo(pos, patient.Pos));
                if (now - brain.StepAt < 900) return;
                if (distance > HealRange)
                {
                    EndHeal(brain, now, false, 6000);
                    return;
                }
                brain.Kit = SpawnMedkit(brain.World, patient, now);
                brain.Stage = Stage.Wait;
                brain.StepAt = now;
                break;

            case Stage.Wait:
                npc.Face(YawTo(pos, patient.Pos));
                bool taken = brain.Kit?.Taken ?? false;
                if (taken || now - brain.StepAt >= 2200) EndHeal(brain, now, taken, 8000);
                break;
        }
    }

    void EndHeal(Brain brain, long now, bool success, long failCooldownMs)
    {
        SetHolding(brain.Npc, null);
        ReleaseKit(brain, now);
        int patient = brain.HealActor;
        brain.HealActor = -1;

        if (success)
        {
            brain.HealFails = 0;
            brain.HealReadyAt = now + HealCooldownMs;
            if (patient >= 0) TrackOf(patient).Hp = 100f;
            Speak(brain, Say.Healed, null, urgent: true);
        }
        else
        {
            brain.HealFails = Math.Min(brain.HealFails + 1, 6);
            brain.HealReadyAt = now + Math.Min(failCooldownMs * brain.HealFails, HealCooldownMs);
        }

        if (brain.Mode != Mode.Heal) return;
        brain.Npc.Stop();
        if (brain.FollowActor >= 0)
        {
            Enter(brain, Mode.Follow, now);
        }
        else
        {
            Enter(brain, Mode.Idle, now);
            brain.NextAction = now + 1500;
        }
    }

    Medkit? SpawnMedkit(IWorld world, Snap patient, long now)
    {
        try
        {
            IWorldObject pickup = world.Add(
                "PickupItemSpawner",
                patient.Pos + new Vector3(0f, 0.6f, 0f),
                new Dictionary<string, object> { ["itemType"] = (int)AvatarItem.Health },
                scale: new Vector3(1.5f, 1.5f, 1.5f));
            var kit = new Medkit(world, pickup.Id, patient.Actor, now + 2600);
            _medkits.Add(kit);
            return kit;
        }
        catch (Exception error)
        {
            _server?.Log($"scientist: could not place a health pickup: {error.Message}");
            return null;
        }
    }

    static void ReleaseKit(Brain brain, long now)
    {
        if (brain.Kit is null) return;
        brain.Kit.ExpiresAt = now;
        brain.Kit = null;
    }

    void UpdateMedkits(long now)
    {
        for (int i = _medkits.Count - 1; i >= 0; i--)
        {
            Medkit kit = _medkits[i];
            if (!kit.Taken && now < kit.ExpiresAt) continue;
            RemoveObject(kit.World, kit.Id);
            _medkits.RemoveAt(i);
        }
    }

    void Social(Brain brain, List<Snap> snaps, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;

        foreach (Snap snap in snaps)
        {
            if (!snap.Alive || IsProvoked(brain, snap.Actor, now)) continue;
            if (MathF.Abs(snap.Pos.Y - pos.Y) > 1.7f || Dist2D(snap.Pos, pos) > TouchRadius) continue;
            Touched(brain, snap, pos, now);
            break;
        }

        if (now < brain.NextSense) return;
        brain.NextSense = now + 300;

        foreach (Snap snap in snaps)
        {
            if (!snap.Alive || brain.Greeted.Contains(snap.Actor) || IsProvoked(brain, snap.Actor, now)) continue;
            if (Dist2D(snap.Pos, pos) > NoticeRange || !CanSee(brain, snap, now)) continue;
            if (!Speak(brain, Say.Hello, snap.Player.Name)) break;

            brain.Greeted.Add(snap.Actor);
            if (brain.Mode == Mode.Wander)
            {
                npc.Stop();
                Enter(brain, Mode.Idle, now);
            }
            if (brain.Mode == Mode.Idle)
            {
                npc.Face(YawTo(pos, snap.Pos));
                brain.NextAction = now + 2500;
            }
            return;
        }

        if (CheckHurt(brain, snaps, pos, now)) return;

        Snap? near = NearestSnap(snaps, pos, NoticeRange);
        if (near is null || brain.Mode != Mode.Idle)
        {
            brain.StareSince = 0;
            return;
        }

        if (Dist2D(near.Pos, pos) <= 3.5f && near.Speed < 0.6f)
        {
            if (brain.StareSince == 0)
            {
                brain.StareSince = now;
            }
            else if (now - brain.StareSince > 5000 && now >= brain.NextStare && Speak(brain, Say.Stare, near.Player.Name))
            {
                brain.NextStare = now + 30_000;
                brain.StareSince = 0;
                npc.Face(YawTo(pos, near.Pos));
            }
        }
        else
        {
            brain.StareSince = 0;
        }

        if (now < brain.NextChat) return;
        brain.NextChat = now + Between(25_000, 60_000);
        Chat(brain, near, pos, now);
    }

    void Touched(Brain brain, Snap snap, Vector3 pos, long now)
    {
        INpc npc = brain.Npc;
        npc.Face(YawTo(pos, snap.Pos));
        if (brain.Mode == Mode.Follow) return;

        bool talking = now - brain.SpokeAt < 2000;
        if (npc.Walking)
        {
            npc.Stop();
            Enter(brain, Mode.Idle, now);
        }
        brain.TouchUntil = now + 1200;
        if (talking || snap.Speed <= PushSpeed || now - brain.LastPush < 2500) return;

        brain.LastPush = now;
        brain.PendingStep = true;
        brain.PushFrom = snap.Pos;
        brain.StepAt = now + 700;
        if (Random.Shared.Next(100) < 30) Speak(brain, Say.Touch, snap.Player.Name);
    }

    void Chat(Brain brain, Snap listener, Vector3 pos, long now)
    {
        Brain? friend = FriendNear(brain, pos);
        if (friend is not null && Random.Shared.Next(100) < 75)
        {
            if (!Speak(brain, Say.Question, null)) return;
            brain.Npc.Face(YawTo(pos, friend.Npc.Position));
            _later.Add(new Later(now + 2800, () =>
            {
                if (friend.Dead || !friend.Npc.Exists || !friend.Npc.Alive) return;
                friend.Npc.Face(YawTo(friend.Npc.Position, brain.Npc.Position));
                Speak(friend, Say.Answer, null);
            }));
            return;
        }
        Speak(brain, Say.Idle, listener.Player.Name);
    }

    Brain? FriendNear(Brain brain, Vector3 pos)
    {
        Brain? best = null;
        float bestDistance = 9f;
        foreach (Brain other in _brains)
        {
            if (ReferenceEquals(other, brain) || !ReferenceEquals(other.World, brain.World)) continue;
            if (other.Dead || !other.Npc.Exists || !other.Npc.Alive || other.Mode != Mode.Idle) continue;
            float distance = Dist2D(other.Npc.Position, pos);
            if (distance > bestDistance) continue;
            best = other;
            bestDistance = distance;
        }
        return best;
    }

    void Wellbeing(Brain brain, long now)
    {
        INpc npc = brain.Npc;
        float fraction = npc.MaxHealth > 0f ? npc.Health / npc.MaxHealth : 1f;

        if (brain.Mode is Mode.Idle or Mode.Wander or Mode.Hide)
        {
            if (fraction <= 0.5f)
            {
                if (!brain.SaidMortal && Speak(brain, Say.Mortal, null))
                {
                    brain.SaidMortal = true;
                    brain.SaidWound = true;
                }
            }
            else if (fraction <= 0.75f)
            {
                if (!brain.SaidWound && Speak(brain, Say.Wound, null)) brain.SaidWound = true;
            }
        }

        if (fraction >= 0.999f || brain.Mode != Mode.Idle || RecoverSeconds <= 0)
        {
            brain.HurtSince = 0;
            return;
        }
        if (brain.HurtSince == 0)
        {
            brain.HurtSince = now;
        }
        else if (now - brain.HurtSince >= RecoverSeconds * 1000L && now - brain.LastPain > 20_000)
        {
            npc.Respawn(npc.Position);
            brain.HurtSince = 0;
            brain.SaidWound = false;
            brain.SaidMortal = false;
        }
    }
}
