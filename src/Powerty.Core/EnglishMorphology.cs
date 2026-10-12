namespace Powerty.Core;

public static class EnglishMorphology
{
    private const int MaxCandidates = 8;

    // Common irregular verb forms / irregular plurals / irregular comparatives
    // that the rule-based stripping below cannot recover. Only consulted when the
    // exact form is missing from the dictionary.
    private static readonly Dictionary<string, string> IrregularForms = new(StringComparer.Ordinal)
    {
        ["went"] = "go", ["gone"] = "go", ["ran"] = "run", ["bought"] = "buy", ["thought"] = "think",
        ["caught"] = "catch", ["gave"] = "give", ["given"] = "give", ["took"] = "take", ["taken"] = "take",
        ["made"] = "make", ["said"] = "say", ["told"] = "tell", ["felt"] = "feel", ["kept"] = "keep",
        ["left"] = "leave", ["meant"] = "mean", ["met"] = "meet", ["paid"] = "pay", ["sold"] = "sell",
        ["sent"] = "send", ["sat"] = "sit", ["slept"] = "sleep", ["spoke"] = "speak", ["spoken"] = "speak",
        ["spent"] = "spend", ["stood"] = "stand", ["swam"] = "swim", ["swum"] = "swim", ["taught"] = "teach",
        ["threw"] = "throw", ["thrown"] = "throw", ["understood"] = "understand", ["woke"] = "wake",
        ["woken"] = "wake", ["wore"] = "wear", ["worn"] = "wear", ["won"] = "win", ["wrote"] = "write",
        ["written"] = "write", ["ate"] = "eat", ["eaten"] = "eat", ["drank"] = "drink", ["drunk"] = "drink",
        ["began"] = "begin", ["begun"] = "begin", ["chose"] = "choose", ["chosen"] = "choose",
        ["drove"] = "drive", ["driven"] = "drive", ["fell"] = "fall", ["fallen"] = "fall",
        ["forgot"] = "forget", ["forgotten"] = "forget", ["got"] = "get", ["gotten"] = "get",
        ["knew"] = "know", ["known"] = "know", ["lost"] = "lose", ["rode"] = "ride", ["ridden"] = "ride",
        ["rose"] = "rise", ["risen"] = "rise", ["saw"] = "see", ["seen"] = "see", ["sang"] = "sing",
        ["sung"] = "sing", ["stole"] = "steal", ["stolen"] = "steal", ["broke"] = "break",
        ["broken"] = "break", ["brought"] = "bring", ["built"] = "build", ["came"] = "come",
        ["did"] = "do", ["done"] = "do", ["drew"] = "draw", ["drawn"] = "draw", ["flew"] = "fly",
        ["flown"] = "fly", ["grew"] = "grow", ["grown"] = "grow", ["heard"] = "hear", ["hid"] = "hide",
        ["hidden"] = "hide", ["held"] = "hold", ["led"] = "lead", ["shone"] = "shine",
        ["showed"] = "show", ["shown"] = "show", ["sped"] = "speed", ["stuck"] = "stick",
        ["struck"] = "strike", ["swore"] = "swear", ["sworn"] = "swear", ["bore"] = "bear",
        ["borne"] = "bear", ["beaten"] = "beat", ["became"] = "become", ["become"] = "become",
        ["bent"] = "bend", ["bit"] = "bite", ["bitten"] = "bite", ["bled"] = "bleed", ["blew"] = "blow",
        ["blown"] = "blow", ["bred"] = "breed", ["clung"] = "cling", ["crept"] = "creep",
        ["dealt"] = "deal", ["dug"] = "dig", ["dove"] = "dive", ["fed"] = "feed", ["fought"] = "fight",
        ["fled"] = "flee", ["flung"] = "fling", ["forbade"] = "forbid", ["froze"] = "freeze",
        ["frozen"] = "freeze", ["hung"] = "hang", ["knelt"] = "kneel", ["laid"] = "lay",
        ["leapt"] = "leap", ["lent"] = "lend", ["lit"] = "light", ["mistook"] = "mistake",
        ["mistaken"] = "mistake", ["pled"] = "plead", ["proven"] = "prove", ["rang"] = "ring",
        ["rung"] = "ring", ["repaid"] = "repay", ["sought"] = "seek", ["sewn"] = "sew",
        ["shook"] = "shake", ["shaken"] = "shake", ["shrank"] = "shrink", ["shrunk"] = "shrink",
        ["sank"] = "sink", ["sunk"] = "sink", ["slew"] = "slay", ["slid"] = "slide", ["slung"] = "sling",
        ["sown"] = "sow", ["spun"] = "spin", ["spat"] = "spit", ["sprang"] = "spring",
        ["sprung"] = "spring", ["stank"] = "stink", ["stunk"] = "stink", ["stung"] = "sting",
        ["strewed"] = "strew", ["strewn"] = "strew", ["strode"] = "stride", ["stridden"] = "stride",
        ["strove"] = "strive", ["striven"] = "strive", ["swept"] = "sweep", ["swung"] = "swing",
        ["tore"] = "tear", ["torn"] = "tear", ["trod"] = "tread", ["trodden"] = "tread",
        ["upheld"] = "uphold", ["wove"] = "weave", ["woven"] = "weave", ["wept"] = "weep",
        ["withdrew"] = "withdraw", ["withdrawn"] = "withdraw", ["withheld"] = "withhold",
        ["wrung"] = "wring", ["forgave"] = "forgive", ["forgiven"] = "forgive", ["arose"] = "arise",
        ["arisen"] = "arise", ["awoke"] = "awake", ["overcame"] = "overcome", ["undertook"] = "undertake",
        ["undertaken"] = "undertake", ["spelt"] = "spell", ["learnt"] = "learn", ["burnt"] = "burn",
        ["sunburnt"] = "sunburn", ["children"] = "child", ["men"] = "man", ["women"] = "woman",
        ["feet"] = "foot", ["teeth"] = "tooth", ["geese"] = "goose", ["mice"] = "mouse",
        ["people"] = "person", ["oxen"] = "ox", ["leaves"] = "leaf", ["lives"] = "life",
        ["knives"] = "knife", ["wives"] = "wife", ["wolves"] = "wolf", ["shelves"] = "shelf",
        ["halves"] = "half", ["thieves"] = "thief", ["scarves"] = "scarf", ["calves"] = "calf",
        ["loaves"] = "loaf", ["selves"] = "self", ["elves"] = "elf", ["dwarves"] = "dwarf",
        ["better"] = "good", ["best"] = "good", ["worse"] = "bad", ["worst"] = "bad",
        ["further"] = "far", ["furthest"] = "far", ["farther"] = "far", ["farthest"] = "far",
        ["elder"] = "old", ["eldest"] = "old", ["less"] = "little", ["least"] = "little",
        ["more"] = "much", ["most"] = "much"
    };

    // Chat abbreviations frequently used in instant messaging; each expands to a
    // full word or phrase that the dictionary can translate.
    private static readonly Dictionary<string, string> SlangExpansions = new(StringComparer.Ordinal)
    {
        ["thx"] = "thanks", ["thnx"] = "thanks", ["tysm"] = "thank you", ["ty"] = "thank you",
        ["pls"] = "please", ["plz"] = "please", ["btw"] = "by the way", ["idk"] = "i don't know",
        ["imo"] = "in my opinion", ["tbh"] = "to be honest", ["fyi"] = "for your information",
        ["nvm"] = "never mind", ["brb"] = "be right back", ["gtg"] = "got to go",
        ["omg"] = "oh my god", ["asap"] = "as soon as possible", ["np"] = "no problem",
        ["yw"] = "you're welcome", ["lol"] = "laugh out loud", ["gonna"] = "going to",
        ["wanna"] = "want to", ["gotta"] = "have to", ["dunno"] = "i don't know",
        ["kinda"] = "kind of", ["sorta"] = "sort of", ["u"] = "you", ["ur"] = "your"
    };

    public static IReadOnlyList<string> Candidates(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return [];
        }

        var candidate = word.Trim().ToLowerInvariant();
        if (candidate.Length == 0)
        {
            return [];
        }

        foreach (var character in candidate)
        {
            if (!char.IsAsciiLetterLower(character) && character is not ('\'' or '’' or '-'))
            {
                return [];
            }
        }

        var results = new List<string>(MaxCandidates);
        void Add(string? value)
        {
            if (value is null)
            {
                return;
            }

            var trimmed = value.Trim('\'', '’', '-');
            if (trimmed.Length < 2 || trimmed == candidate || results.Contains(trimmed))
            {
                return;
            }

            results.Add(trimmed);
        }

        if (IrregularForms.TryGetValue(candidate, out var irregularBase))
        {
            Add(irregularBase);
        }

        if (SlangExpansions.TryGetValue(candidate, out var slangExpansion))
        {
            Add(slangExpansion);
        }

        if (candidate.Length < 3)
        {
            return results;
        }

        if (candidate.EndsWith("'s") || candidate.EndsWith("’s"))
        {
            Add(candidate[..^2]);
        }

        if (candidate.EndsWith("s'") || candidate.EndsWith("s’"))
        {
            Add(candidate[..^1]);
        }

        if (candidate.EndsWith("ily") && candidate.Length > 4)
        {
            Add(candidate[..^3] + "y");
        }

        if (candidate.EndsWith("ies") && candidate.Length > 3)
        {
            Add(candidate[..^3] + "y");
        }

        if (candidate.EndsWith("ied") && candidate.Length > 3)
        {
            Add(candidate[..^3] + "y");
        }

        if (candidate.EndsWith("ier") && candidate.Length > 3)
        {
            Add(candidate[..^3] + "y");
        }

        if (candidate.EndsWith("iest") && candidate.Length > 4)
        {
            Add(candidate[..^4] + "y");
        }

        if (candidate.EndsWith("es") && candidate.Length > 2)
        {
            Add(candidate[..^2]);
            Add(candidate[..^1]);
        }
        else if (candidate.EndsWith("s") && candidate.Length > 2 &&
                 !candidate.EndsWith("ss") && !candidate.EndsWith("us") && !candidate.EndsWith("is"))
        {
            Add(candidate[..^1]);
        }

        if (candidate.EndsWith("ed") && candidate.Length > 2)
        {
            var stem = candidate[..^2];
            Add(stem);
            Add(candidate[..^1]);
            if (stem.Length >= 3 && stem[^1] == stem[^2])
            {
                Add(stem[..^1]);
            }
        }

        if (candidate.EndsWith("ing") && candidate.Length > 3)
        {
            var stem = candidate[..^3];
            Add(candidate[..^4] + "ie");
            Add(stem);
            Add(stem + "e");
            if (stem.Length >= 2 && stem[^1] == stem[^2])
            {
                Add(stem[..^1]);
            }
        }

        if (candidate.EndsWith("er") && candidate.Length > 2)
        {
            var stem = candidate[..^2];
            Add(stem);
            Add(candidate[..^1]);
            if (stem.Length >= 3 && stem[^1] == stem[^2])
            {
                Add(stem[..^1]);
            }
        }

        if (candidate.EndsWith("est") && candidate.Length > 3)
        {
            var stem = candidate[..^3];
            Add(stem);
            Add(candidate[..^4] + "e");
            if (stem.Length >= 3 && stem[^1] == stem[^2])
            {
                Add(stem[..^1]);
            }
        }

        if (candidate.EndsWith("ally") && candidate.Length > 4)
        {
            Add(candidate[..^4]);
            Add(candidate[..^4] + "al");
        }

        if (candidate.EndsWith("lly") && candidate.Length > 3)
        {
            // fully -> full (drop the trailing y; the plain -ly rule would give "ful")
            Add(candidate[..^1]);
        }

        if (candidate.EndsWith("ly") && candidate.Length > 3)
        {
            Add(candidate[..^2]);
        }

        return results;
    }
}
