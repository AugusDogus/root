namespace RootEngineProbe;

// Match configuration lives separately from navigation and Steam invitations.
internal sealed class NativeSetupMenu
{
    private readonly NativeMenu view;
    private readonly Action<MatchSetup> start;
    private readonly Action back;
    private MatchSetup setup = new();
    private string message = "Choose your faction and five seats for friends or Clockwork bots.";

    public NativeSetupMenu(NativeMenu view, Action<MatchSetup> start, Action back)
    { this.view = view; this.start = start; this.back = back; }

    public void Show()
    {
        view.Begin(true, "setup");
        view.Text("Set up your game", 220, 55, 840, 60, 38);
        view.Text(message, 140, 112, 1000, 65, 21);
        for (var seat = 0; seat < 6; seat++)
        {
            var index = seat;
            var prefix = seat == 0 ? "You: " : $"{seat + 1}. ";
            view.Button(prefix + MatchSetup.FactionName(setup.Factions[seat]) + (MatchSetup.IsBot(setup.Factions[seat]) ? " (bot)" : ""), 70, 190 + seat * 70, 535, 58, () => Faction(index));
        }
        view.Button("Map: " + MatchSetup.Maps[setup.Map], 675, 190, 535, 58, Maps);
        view.Button("Deck: " + MatchSetup.Decks[setup.Deck], 675, 260, 535, 58, () => { setup = setup with { Deck = 1 - setup.Deck }; Show(); });
        view.Button("Characters & Clockwork", 675, 330, 535, 58, Characters);
        view.Button($"Landmarks: {setup.Landmarks.Length}", 675, 400, 535, 58, Landmarks);
        view.Button($"Hirelings: {setup.Hirelings.Length}", 675, 470, 535, 58, Hirelings);
        view.Button("Setup: " + (setup.AdvancedSetup ? "Advanced (chosen factions)" : "Standard"), 675, 540, 535, 58,
            () => { setup = setup with { AdvancedSetup = !setup.AdvancedSetup }; Show(); });
        view.Button("Back", 140, 675, 300, 60, back);
        view.Button("Start game", 760, 675, 380, 60, () =>
        {
            if (NativeMatchSetup.Validate(setup) is { } error) { message = error; Show(); return; }
            start(setup);
        });
    }

    private void Faction(int seat, bool bots = false)
    {
        Page(seat == 0 ? "Choose your faction" : $"Choose faction for seat {seat + 1}", "Selecting an occupied faction swaps the two seats.");
        var index = 0;
        foreach (var faction in MatchSetup.PlayerFactions)
        {
            if (MatchSetup.IsBot(faction.Id) != bots) continue;
            if (MatchSetup.IsBot(setup.Factions[seat]) && faction.Id == setup.Factions[0]) continue;
            if (!bots && !lib.data.FactionUtils.ReleasedPlayerFactions.Contains((tuber_canis.data.Factions)faction.Id)) continue;
            Choice(faction.Name, index++, () =>
            {
                var roster = setup.Factions.ToArray();
                var characters = setup.Characters.ToArray();
                var traits = setup.BotTraits.ToArray();
                var occupied = Array.IndexOf(roster, faction.Id);
                if (occupied >= 0)
                {
                    (roster[occupied], roster[seat]) = (roster[seat], roster[occupied]);
                    (characters[occupied], characters[seat]) = (characters[seat], characters[occupied]);
                    (traits[occupied], traits[seat]) = (traits[seat], traits[occupied]);
                }
                else { roster[seat] = faction.Id; characters[seat] = 0; traits[seat] = Array.Empty<int>(); }
                setup = setup with { Factions = roster, Characters = characters, BotTraits = traits };
                message = "Choose your faction and five seats for friends or Clockwork bots.";
                Show();
            });
        }
        if (seat != 0)
            view.Button(bots ? "Human factions" : "Clockwork bots", 760, 685, 380, 55, () => Faction(seat, !bots));
        if (bots)
            view.Button("Difficulty: " + MatchSetup.BotDifficulties[setup.BotDifficulty], 300, 430, 680, 60,
                () => { setup = setup with { BotDifficulty = (setup.BotDifficulty + 1) % 4 }; Faction(seat, true); });
    }

    private void Maps()
    {
        Page("Choose a map", "Everyone joins on the host's map.");
        for (var index = 0; index < MatchSetup.Maps.Length; index++)
        {
            var map = index;
            Choice(MatchSetup.Maps[index], index, () =>
            {
                setup = setup with { Map = map, Landmarks = setup.Landmarks.Where(id => !(map == 2 && id == 1 || map == 3 && id == 0)).ToArray() };
                Show();
            });
        }
    }

    private void Characters()
    {
        Page("Characters & Clockwork", "Choose Vagabond characters and optional Clockwork traits.");
        var row = 0;
        for (var seat = 0; seat < 6; seat++)
        {
            var index = seat;
            if (MatchSetup.IsBot(setup.Factions[seat]))
            {
                Choice(MatchSetup.FactionName(setup.Factions[seat]) + $": {setup.BotTraits[seat].Length} traits", row++, () => Clockwork(index));
                continue;
            }
            if (setup.Factions[seat] is not (3 or 5)) continue;
            Choice(MatchSetup.FactionName(setup.Factions[seat]) + ": " + MatchSetup.Vagabonds[setup.Characters[seat]], row++, () => Character(index));
        }
        if (row == 0) view.Text("Choose a Vagabond or Clockwork faction first.", 260, 320, 760, 70, 27);
    }

    private void Clockwork(int seat)
    {
        Page(MatchSetup.FactionName(setup.Factions[seat]), "Choose any optional traits. Difficulty applies to all Clockwork bots.", Characters);
        for (var index = 0; index < 4; index++)
        {
            var trait = index;
            Choice((setup.BotTraits[seat].Contains(index) ? "Selected: " : "") + MatchSetup.TraitNames[setup.Factions[seat] - 10][index], index, () =>
            {
                var traits = setup.BotTraits.ToArray();
                traits[seat] = traits[seat].Contains(trait) ? traits[seat].Where(value => value != trait).ToArray() : traits[seat].Append(trait).ToArray();
                setup = setup with { BotTraits = traits };
                Clockwork(seat);
            });
        }
        view.Button("Difficulty: " + MatchSetup.BotDifficulties[setup.BotDifficulty], 300, 400, 680, 60,
            () => { setup = setup with { BotDifficulty = (setup.BotDifficulty + 1) % 4 }; Clockwork(seat); });
        if (setup.Factions[seat] == 13)
            view.Button("Character: " + MatchSetup.Vagabonds[setup.VagabotCharacter], 300, 500, 680, 60,
                () => { setup = setup with { VagabotCharacter = setup.VagabotCharacter % 3 + 1 }; Clockwork(seat); });
    }

    private void Character(int seat)
    {
        Page("Choose a Vagabond", "All nine characters are available.", Characters);
        for (var index = 0; index < MatchSetup.Vagabonds.Length; index++)
        {
            var character = index;
            Choice(MatchSetup.Vagabonds[index], index, () =>
            {
                var characters = setup.Characters.ToArray();
                characters[seat] = character;
                setup = setup with { Characters = characters };
                Characters();
            });
        }
    }

    private void Landmarks()
    {
        Page("Choose landmarks", "Choose up to two. The lake's Ferry and mountain's Tower are already included.");
        for (var index = 0; index < MatchSetup.LandmarkNames.Length; index++)
        {
            var landmark = index;
            if (setup.Map == 2 && index == 1 || setup.Map == 3 && index == 0) continue;
            Choice((setup.Landmarks.Contains(index) ? "Selected: " : "") + MatchSetup.LandmarkNames[index], index, () =>
            {
                if (setup.Landmarks.Contains(landmark)) setup = setup with { Landmarks = setup.Landmarks.Where(id => id != landmark).ToArray() };
                else if (setup.Landmarks.Length < 2) setup = setup with { Landmarks = setup.Landmarks.Append(landmark).ToArray() };
                Landmarks();
            });
        }
    }

    private void Hirelings()
    {
        Page("Choose hirelings", "Choose none or exactly three. All are demoted in a six-player game.");
        var index = 0;
        foreach (var hireling in MatchSetup.HirelingNames)
        {
            var faction = (tuber_canis.data.Factions)hireling.Id;
            if (!lib.data.FactionUtils.ReleasedHirelings.Contains(faction)) continue;
            if (lib.data.FactionUtils.mutuallyExclusiveFactions.TryGetValue(faction, out var excludes) &&
                setup.Factions.Any(id => excludes.Contains((tuber_canis.data.Factions)id))) continue;
            Choice((setup.Hirelings.Contains(hireling.Id) ? "Selected: " : "") + hireling.Name, index++, () =>
            {
                if (setup.Hirelings.Contains(hireling.Id)) setup = setup with { Hirelings = setup.Hirelings.Where(id => id != hireling.Id).ToArray() };
                else if (setup.Hirelings.Length < 3) setup = setup with { Hirelings = setup.Hirelings.Append(hireling.Id).ToArray() };
                Hirelings();
            });
        }
        view.Button("Clear hirelings", 760, 685, 380, 55, () => { setup = setup with { Hirelings = Array.Empty<int>() }; Hirelings(); });
    }

    private void Page(string title, string subtitle, Action? backTo = null)
    {
        view.Begin(true, "setup-options");
        view.Text(title, 200, 55, 880, 60, 36);
        view.Text(subtitle, 120, 115, 1040, 65, 20);
        view.Button("Back", 140, 685, 300, 55, backTo ?? Show);
    }

    private void Choice(string label, int index, Action action) =>
        view.Button(label, index % 2 == 0 ? 70 : 675, 190 + index / 2 * 65, 535, 55, action);
}
