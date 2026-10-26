namespace HalfLife;
enum Say
{
    Hello, Idle, Question, Answer, Stare, Follow, Wait, Refuse, Fear, Scream, Pain,
    CureA, CureB, CureC, Heal, Healed, Fine, Later, Wound, Mortal, Death, Stop, Startle, Touch, Lost,
}

static class Lines
{
    static readonly Dictionary<Say, string[]> Table = new()
    {
        // the first time he notices a player
        [Say.Hello] =
        [
            "Hello, {player}.",
            "Ah, {player}! Good to see you.",
            "Ah, it's good to see you!",
            "Greetings!",
            "Hello there!",
        ],
        // while a player is around domewhere
        [Say.Idle] =
        [
            "Why do we all have to wear these ridiculous ties?",
            "Yes, this all looks nominal.",
            "As I expected.",
            "Someone has hidden my glasses again.",
            "I hope those containment parameters are still nominal.",
            "Most of the simulation results are perfectly acceptable, you know.",
            "That's odd.",
            "I wonder if I should run that test again.",
            "I wonder what the cafeteria is serving today.",
        ],
        // two scientists chatting
        [Say.Question] =
        [
            "Did you hear that noise just now?",
            "Have you seen the latest test results?",
            "Is it lunchtime yet?",
            "Do you think the new equipment will be installed on time?",
        ],
        [Say.Answer] =
        [
            "I'm sure it's nothing.",
            "I haven't had a chance to look at them yet.",
            "Don't ask me, I only work here.",
            "Let's hope so.",
        ],
        // a player stands next to him and just looks at him
        [Say.Stare] =
        [
            "Excuse me, {player}.",
            "Is there something on my face?",
            "You're making me a little nervous, standing there like that.",
            "Yes? Did you need something?",
        ],
        // agrees to follow a player
        [Say.Follow] =
        [
            "Lead the way.",
            "Okay, let's go.",
            "I certainly hope you know what you're doing.",
            "Right. I'll follow you.",
        ],
        // told to stop following
        [Say.Wait] =
        [
            "Right. I will refuse taking another step.",
            "I don't suppose you'd reconsider?",
            "Fine, I'll just wait right here.",
        ],
        // refuses a player who hurt him (or is too scared to move)
        [Say.Refuse] =
        [
            "Leave me alone!",
            "Stay away from me, {player}!",
            "I'm not going anywhere with you!",
        ],
        // afraid of the player who just attacked him
        [Say.Fear] =
        [
            "STOP! I'm from science team!",
            "{player}! What are you doing?!",
            "Please, {player}, don't shoot!",
            "Stop it! I'm on your side!",
            "Are you out of your mind, {player}?!",
            "Get away from me, {player}!",
            "Someone stop {player}, they've gone mad!",
        ],
        // running for cover
        [Say.Scream] =
        [
            "AAAAAAAH!",
            "Help! Somebody help me!",
            "No! I don't want to die!",
            "I just want to get out of this alive.",
            "My goodness!",
            "NO!",
        ],
        // the pain sounds
        [Say.Pain] =
        [
            "STOP!",
            "NOO!",
            "STAY BACK!",
            "AHH, MY LEG!",
            "STOP! PLEASE!",
        ],
        //  A B C: comments on a hurt player (half, a quarter and an eighth of their health)
        [Say.CureA] =
        [
            "It appears that you need medical attention right away.",
            "Ahh! {player}, you've been wounded.",
        ],
        [Say.CureB] =
        [
            "That looks serious, {player}. Don't move!",
            "Good heavens, that's a nasty wound!",
        ],
        [Say.CureC] =
        [
            "{player}, you're barely standing! Sit down!",
            "You're in terrible shape, {player}!",
        ],
        // just before he treats a player
        [Say.Heal] =
        [
            "I can give you something for the pain.",
            "Don't worry, I am a doctor.",
            "Hold still, this won't hurt a bit.",
            "Allow me to treat that injury.",
        ],
        [Say.Healed] =
        [
            "There. Good as new.",
            "There you go!",
            "Done. You should feel much better now.",
        ],
        [Say.Fine] =
        [
            "You look perfectly healthy to me, {player}.",
            "I don't see anything wrong with you.",
            "Nothing a little rest wouldn't fix. You're fine.",
        ],
        // cooldown
        [Say.Later] =
        [
            "Give me a minute, {player}. I'm still restocking.",
            "I just treated you. Give it a minute.",
        ],
        // about his own health (three quarters and half)
        [Say.Wound] =
        [
            "I seem to be wounded... but I can keep going.",
            "Well, I seem to be seriously wounded...",
        ],
        [Say.Mortal] =
        [
            "I don't feel so good...",
            "I don't think I can last much longer...",
        ],
        [Say.Death] =
        [
            "Mom, I'm sorry...",
            "I... Am dying...",
            "I was just a scientist...",
        ],
        // a player is attacking one of his colleagues
        [Say.Stop] =
        [
            "My God, what are you doing?",
            "Stop attacking, he's a friend!",
            "Are you insane? Stop attacking, immediately!",
        ],
        // startled by gunfire nearby
        [Say.Startle] =
        [
            "What was that?!",
            "Was that a gunshot?!",
            "Get down!",
            "Oh no.",
        ],
        // bumped into
        [Say.Touch] =
        [
            "Excuse me.",
            "Oh! {player}",
            "Watch it, please.",
        ],
        // the player he was following got away from him
        [Say.Lost] =
        [
            "Why are you leaving me here?",
            "Have I been that much of a burden?",
        ],
    };

    static readonly Dictionary<Say, int> Last = new();

    public static string Pick(Say say, string player)
    {
        string[] options = Table[say];
        int index = Random.Shared.Next(options.Length);
        lock (Last)
        {
            if (options.Length > 1 && Last.TryGetValue(say, out int previous) && previous == index)
                index = (index + 1 + Random.Shared.Next(options.Length - 1)) % options.Length;
            Last[say] = index;
        }
        return options[index].Replace("{player}", player);
    }
}
