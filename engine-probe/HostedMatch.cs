using Canis.actions;
using Canis.json;
using Canis.json.events;
using Canis.matchThread;
using Canis.utils.ids;
using Networking.selection.messages;
using Networking.selection.messages.outgoing;
using Networking.selection.targetinformation;
using Networking.selection.targetresponse;
using tuber.canis;
using tuber.canis.data.matchinitdata;
using tuber_canis.data;
using System.Security.Cryptography;

namespace RootEngineProbe;

internal sealed record SelectionOffer(int Counter, string Prompt, string Source, string[] Targets);

internal enum ChoiceResult
{
    Accepted, NoSuchSelection, WrongPlayer, UnsupportedSelection, InvalidSource, InvalidTarget, EngineDidNotAdvance
}

// All calls, including message dispatch, run on the game's main thread.
internal sealed class HostedMatch
{
    public TuberMatch Match { get; }
    // Drafting replaces the temporary player entity when a faction is chosen.
    // Resolve through the stable account each time, including privacy checks.
    public tuber.canis.entities.TuberPlayerEntity[] SeatPlayers =>
        SeatInitialization.Select(player => Match.PlayersMap[player.accountID]).ToArray();
    public TuberPlayerMatchInitData[] SeatInitialization { get; }
    public MatchThreadV2 Thread { get; }
    public string CheckpointInitialization { get; }
    public SeatHistory[] Messages { get; }
    private readonly string?[] completionMessages;
    public string?[] CompletionMessages => completionMessages.ToArray();
    public int PlayerCount => Messages.Length;
    public MatchSetup PublicSetup => NativeMatchSetup.Read(Match.TuberMatchInitData, SeatInitialization) with
    { Factions = SeatPlayers.Select(player => (int)player.Faction).ToArray() };
    public MatchLobby Lobby { get; } = new();
    public MatchChat Chat { get; } = new();
    public NativeTurnTimers Timers { get; } = new();
    private Il2CppSystem.Threading.Tasks.Task? resignation;
    private readonly HashSet<int> aiSelections = new();
    public bool IsHumanSeat(int seat) => NativeMatchSetup.IsHumanSeat(SeatInitialization[seat]);
    public void AdvanceAIPlayers(Action<Il2CppSystem.Collections.IEnumerator> start)
    {
        if (Resigning || Match.GameOverD) return;
        aiSelections.RemoveWhere(counter => !Thread.HasPendingResponse(counter));
        foreach (var player in SeatPlayers)
        {
            if (!player.IsAI || player.IsClockwork()) continue;
            var counter = Thread.GetCounterForPlayerEntity(player);
            if (!Thread.HasPendingResponse(counter) || !aiSelections.Add(counter)) continue;
            // The private authority replaces the online server's AI scheduler.
            // Root supplies the evaluator, response and native coroutine.
            start(player.SelectFrom(Thread.GetPlayerPendingResponse(counter).Item2.Selection, Match));
        }
    }
    public bool Resigning => resignation is not null;
    public void Resign(int seat)
    {
        if (Match.GameOverD || Match.HasResigned(SeatInitialization[seat].accountID)) return;
        resignation = Match.HandleResignGameAsync(SeatInitialization[seat].accountID, TuberMatch.ResignationType.Resign);
    }
    public bool CompleteResignation()
    {
        if (resignation is not { IsCompleted: true } completed) return false;
        if (completed.IsFaulted || completed.IsCanceled)
            throw new InvalidOperationException($"Native resignation failed. The previous checkpoint is preserved: {completed.Exception}");
        resignation = null;
        return true;
    }

    public void RestoreCompletionMessages(string?[] saved)
    {
        if (saved.Length != PlayerCount) throw new ArgumentException("Saved results do not match the roster.", nameof(saved));
        Array.Copy(saved, completionMessages, saved.Length);
    }

    public string[] Snapshot(int seat)
    {
        var accounts = new Il2CppSystem.Collections.Generic.List<AccountID>();
        accounts.Add(SeatInitialization[seat].accountID);
        var state = new SerializeGameState(Match.TuberPlaymat,
            accounts.Cast<Il2CppSystem.Collections.Generic.IEnumerable<AccountID>>(), Match).GetState();
        state.Entities = Canis.messages.SerializedGameStateObfuscator.Instance.ObfuscateEntity(
            Match, SeatPlayers[seat], state.Entities, Canis.obfuscation.Visibility.Public);
        state = Canis.Match.StripUnusedAttributes(state);
        var messages = new List<string>
        {
            JSON.ToJSON(new Canis.messages.sequence.SequenceMessage(null, state.Cast<Canis.messages.IGameMessage>()), false)
        };
        messages.AddRange(Timers.Snapshot(SeatInitialization[seat].accountID));
        if (completionMessages[seat] is { } completed)
        {
            messages.Add(completed);
            return messages.ToArray();
        }
        if (!IsHumanSeat(seat) || Match.HasResigned(SeatInitialization[seat].accountID)) return messages.ToArray();
        var counter = Thread.GetCounterForPlayerEntity(SeatPlayers[seat]);
        if (Thread.HasPendingResponse(counter))
        {
            var selection = Canis.Match.StripUnusedAttributes(Thread.GetPlayerPendingResponse(counter).Item2.Selection);
            messages.Add(JSON.ToJSON(new Canis.messages.sequence.SequenceMessage(null, selection.Cast<Canis.messages.IGameMessage>()), false));
        }
        return messages.ToArray();
    }

    public HostedMatch(bool sixPlayers) : this(CreateInitialization(sixPlayers)) { }
    public HostedMatch(MatchSetup setup) : this(CreateInitialization(true, setup)) { }

    public HostedMatch(TuberMatchInitData init)
    {
        dwd.core.data.ReflectionTypeInitializer.Initialize();
        Messages = Enumerable.Range(0, init.TuberPlayers.Count).Select(_ => new SeatHistory()).ToArray();
        completionMessages = new string?[init.TuberPlayers.Count];
        var seats = Enumerable.Range(0, init.TuberPlayers.Count)
            .ToDictionary(seat => init.TuberPlayers[seat].accountID.ToString(), seat => seat);
        var chosenFactions = init.TuberPlayers.ToArray().Select(player => player.Faction).ToArray();
        Match = new TuberMatch();
        var started = false;
        Il2CppSystem.Action<AccountID, DWDEvent> dispatcher = new Action<AccountID, DWDEvent>((account, message) =>
        {
            var json = Timers.Observe(account, message);
            if (started && Match.HasResigned(account) &&
                (message.TryCast<Canis.messages.sequence.SequenceMessage>()?.Msg.TryCast<SelectionMessage>() is not null ||
                 message.TryCast<SelectionMessage>() is not null)) return;
            var seat = seats[account.ToString()];
            Messages[seat].Add(json);
            if (message.TryCast<Canis.messages.sequence.SequenceMessage>()?.Msg.TryCast<tuber.canis.messages.GameResults>() is not null)
                completionMessages[seat] = json;
        });
        Match.SetMessageDispatcher(dispatcher);
        Match.Configure(init);
        Timers.Attach(Match);
        NativeTurnTimers.Configure(Match);
        // Root has already converted the timed-out player to AI. Keep their
        // private connection available for watching or returning to the menu.
        Match.SetRemoveIdlePlayer(new Func<AccountID, Il2CppSystem.Threading.Tasks.Task>(_ => Il2CppSystem.Threading.Tasks.Task.CompletedTask));
        Match.messageActionFactory = new ObfuscatedMessageActionFactory().Cast<IMessageActionFactory>();
        NativePrivateVisibility.Install();
        Match.Start();
        started = true;
        // Native setup supplies stable seats before factions are drafted.
        // Older saves identify seats by faction, including swapped Vagabonds.
        var configuredPlayers = Match.TuberMatchInitData.TuberPlayers.ToArray();
        SeatInitialization = configuredPlayers.All(player => player.metadata.ContainsKey(NativeHostConfiguration.SeatKey))
            ? configuredPlayers.OrderBy(player => int.Parse(player.metadata[NativeHostConfiguration.SeatKey], System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : chosenFactions.Select(faction => configuredPlayers.Single(player => player.Faction == faction)).ToArray();
        Messages = SeatInitialization.Select(player => Messages[seats[player.accountID.ToString()]]).ToArray();
        completionMessages = SeatInitialization.Select(player => completionMessages[seats[player.accountID.ToString()]]).ToArray();
        seats.Clear();
        for (var seat = 0; seat < SeatInitialization.Length; seat++) seats.Add(SeatInitialization[seat].accountID.ToString(), seat);
        // Clockwork turns are scripted, but expansion interactions such as
        // returning a destroyed relic use the shared native AI evaluators.
        foreach (var player in Match.Players)
            if (player.IsClockwork() && player.aiProfile.TryCast<tuber.canis.entities.UnassignedAIProfile>() is not null)
                player.aiProfile = new tuber.canis.entities.ai.AIProfile(Match, player);
        Thread = Match.GetThread(MatchThread.MAIN_THREAD).Cast<MatchThreadV2>();
        // Cache configuration without the loaded checkpoint. Serializing the
        // old board and save again on every move makes resumed games sluggish.
        var nativeInit = Match.TuberMatchInitData;
        var savedState = nativeInit.gameState;
        var savedData = nativeInit.saveData;
        try
        {
            nativeInit.gameState = null;
            nativeInit.saveData = null;
            var ordered = JSON.Deserialize<TuberMatchInitData>(JSON.ToJSON(nativeInit, false));
            var players = ordered.TuberPlayers.ToArray().ToDictionary(player => player.accountID.ToString());
            ordered.TuberPlayers.Clear();
            foreach (var player in SeatInitialization) ordered.AddTuberPlayer(players[player.accountID.ToString()]);
            CheckpointInitialization = JSON.ToJSON(ordered, false);
        }
        finally
        {
            nativeInit.gameState = savedState;
            nativeInit.saveData = savedData;
        }
    }

    public static TuberMatchInitData CreateInitialization(bool sixPlayers, MatchSetup? setup = null)
    {
        var init = new TuberMatchInitData(new GameID(Guid.NewGuid().ToString()))
        {
            randomSeed = int.TryParse(Environment.GetEnvironmentVariable("ROOT_LAB_TEST_SEED"), out var seed)
                ? seed : RandomNumberGenerator.GetInt32(int.MaxValue),
            DoNotShufflePlayers = true
        };
        init.AddOption("matchType", "Live");
        if (setup is not null) NativeMatchSetup.Apply(init, setup);
        var factions = setup is not null ? setup.Factions.Select(id => (Factions)id).ToArray() : sixPlayers
            ? new[] { Factions.MarquiseDeCat, Factions.EyrieDynasties, Factions.WoodlandAlliance, Factions.Vagabond, Factions.LizardCult, Factions.RiverfolkCompany }
            : new[] { Factions.MarquiseDeCat, Factions.EyrieDynasties, Factions.WoodlandAlliance, Factions.Vagabond };
        for (var i = 0; i < factions.Length; i++)
        {
            var account = new AccountID(Guid.NewGuid().ToString());
            init.AddTuberPlayer(setup is not null ? NativeMatchSetup.Player($"Player {i + 1}", account, i, setup) : new TuberPlayerMatchInitData($"Lab player {i + 1}", account)
            {
                Faction = factions[i],
                StartingCharacter = setup is null ? VagabondCharacters.Unknown : (VagabondCharacters)setup.Characters[i],
                PlayerStartingOrder = i
            });
        }
        return init;
    }

    public SelectionOffer? GetOffer(int seat)
    {
        if (!IsHumanSeat(seat) || Match.HasResigned(SeatInitialization[seat].accountID)) return null;
        var counter = Thread.GetCounterForPlayerEntity(SeatPlayers[seat]);
        if (Thread.HasPendingResponse(counter) == false)
            return null;
        var selection = Thread.GetPlayerPendingResponse(counter).Item2.Selection.TryCast<SelectionWithTargetsRequired>();
        if (selection is null || selection.SourceID is null || selection.TargetMap is null)
            return null;
        if (selection.TargetMap.TryGetValue(selection.SourceID, out var information) == false || information.Length != 1)
            return null;
        var targets = information[0].TryCast<EntityListTargetInformation>();
        if (targets is null || targets.NumberToSelect != 1)
            return null;
        return new SelectionOffer(counter, selection.Prompt.ID, selection.SourceID.ToString(),
            targets.ValidTargets.Select(id => id.ToString()).ToArray());
    }

    public ChoiceResult Choose(int seat, int counter, string source, string target)
        => ChooseTargets(seat, counter, source, new TargetChoice[] { new TargetChoice.Entities(new[] { target }) });

    public ChoiceResult ChooseTargets(int seat, int counter, string source, IReadOnlyList<TargetChoice> choices)
    {
        if (Thread.HasPendingResponse(counter) == false)
            return ChoiceResult.NoSuchSelection;
        if (Thread.GetPlayerEntityForCounter(counter).Pointer != SeatPlayers[seat].Pointer)
            return ChoiceResult.WrongPlayer;
        var selection = Thread.GetPlayerPendingResponse(counter).Item2.Selection.TryCast<SelectionWithTargetsRequired>();
        if (selection is null) return ChoiceResult.UnsupportedSelection;
        var sourceId = new EntityID(source);
        if (selection.TargetMap.TryGetValue(sourceId, out var information) == false) return ChoiceResult.InvalidSource;
        var validation = TargetChoice.ValidateResponse(information, choices);
        if (validation != ChoiceResult.Accepted) return validation;
        var responses = new Il2CppSystem.Collections.Generic.List<TargetResponse>();
        foreach (var choice in choices) responses.Add(choice.ToNative());
        var response = new SelectionWithTargets(selection, sourceId,
            responses.Cast<Il2CppSystem.Collections.Generic.IEnumerable<TargetResponse>>());
        Match.Write(response, SeatInitialization[seat].accountID);
        return Thread.HasPendingResponse(counter) ? ChoiceResult.EngineDidNotAdvance : ChoiceResult.Accepted;
    }

    public ChoiceResult Pass(int seat, int counter)
    {
        if (Thread.HasPendingResponse(counter) == false) return ChoiceResult.NoSuchSelection;
        if (Thread.GetPlayerEntityForCounter(counter).Pointer != SeatPlayers[seat].Pointer) return ChoiceResult.WrongPlayer;
        var selection = Thread.GetPlayerPendingResponse(counter).Item2.Selection.TryCast<SelectionWithTargetsRequired>();
        if (selection is null || selection.Forced) return ChoiceResult.UnsupportedSelection;
        if (selection.IgnoreFirst)
        {
            if (selection.SourceID is null || selection.TargetMap.TryGetValue(selection.SourceID, out var information) == false)
                return ChoiceResult.UnsupportedSelection;
            // An automatically selected source still requires its forced targets.
            // Optional targets, such as the discard undo prompt, may be skipped.
            foreach (var target in information)
                if (target.Selected && target.TryCast<EntityListTargetInformation>() is not { Forced: false } &&
                    target.TryCast<EntityGroupingTargetInformation>() is not { Forced: false, MinimumToSelect: 0 })
                    return ChoiceResult.UnsupportedSelection;
        }
        var response = Outgoing.Pass(selection);
        Match.Write(response, SeatInitialization[seat].accountID);
        return Thread.HasPendingResponse(counter) ? ChoiceResult.EngineDidNotAdvance : ChoiceResult.Accepted;
    }

    public ChoiceResult ChooseCustom(int seat, int counter, int? choice)
    {
        if (Thread.HasPendingResponse(counter) == false) return ChoiceResult.NoSuchSelection;
        if (Thread.GetPlayerEntityForCounter(counter).Pointer != SeatPlayers[seat].Pointer) return ChoiceResult.WrongPlayer;
        var selection = Thread.GetPlayerPendingResponse(counter).Item2.Selection;
        var archetype = selection.TryCast<ArchetypeCustomChoiceRequired>();
        var custom = selection.TryCast<CustomChoiceRequired>();
        var integer = selection.TryCast<IntChoiceRequired>();
        var count = archetype?.Buttons.Count ?? custom?.Buttons.Length;
        if (count is null && integer is null) return ChoiceResult.UnsupportedSelection;
        if (choice is null)
        {
            if (archetype?.forced != false && integer?.forced != false) return ChoiceResult.InvalidTarget;
        }
        else if (integer is not null)
        {
            if (choice < integer.min || choice > integer.amount) return ChoiceResult.InvalidTarget;
        }
        else if (choice < 0 || choice >= count) return ChoiceResult.InvalidTarget;
        var response = new dwd.core.match.messages.outgoing.GameCustomChoice(Match.TuberMatchInitData.gameID,
            choice is int number ? new Il2CppSystem.Nullable<int>(number) : new Il2CppSystem.Nullable<int>(), counter);
        Match.Write(response, SeatInitialization[seat].accountID);
        return Thread.HasPendingResponse(counter) ? ChoiceResult.EngineDidNotAdvance : ChoiceResult.Accepted;
    }

    public ChoiceResult SetPrices(int seat, int counter, int handCard, int riverboats, int mercenaries)
    {
        if (Thread.HasPendingResponse(counter) == false) return ChoiceResult.NoSuchSelection;
        if (Thread.GetPlayerEntityForCounter(counter).Pointer != SeatPlayers[seat].Pointer) return ChoiceResult.WrongPlayer;
        if (Thread.GetPlayerPendingResponse(counter).Item2.Selection.TryCast<tuber.canis.messages.RiverfolkPricesRequired>() is null)
            return ChoiceResult.UnsupportedSelection;
        if (new[] { handCard, riverboats, mercenaries }.Any(price => price < 1 || price > 4)) return ChoiceResult.InvalidTarget;
        var prices = new Il2CppSystem.Collections.Generic.Dictionary<RiverfolkService, int>();
        prices.Add(RiverfolkService.HandCard, handCard);
        prices.Add(RiverfolkService.Riverboats, riverboats);
        prices.Add(RiverfolkService.Mercenaries, mercenaries);
        Match.Write(new tuber.canis.messages.ChosenRiverfolkPrices(Match.TuberMatchInitData.gameID, prices, counter),
            SeatInitialization[seat].accountID);
        return Thread.HasPendingResponse(counter) ? ChoiceResult.EngineDidNotAdvance : ChoiceResult.Accepted;
    }
}
