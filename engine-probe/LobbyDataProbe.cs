using System.Text.Json;
using Canis.json;
using Canis.utils.ids;
using Networking.game;
using Networking.lobby;
using tuber.client.data;
using tuber.client.match.data;
using tuber_canis.data;
using UnityEngine;

namespace RootEngineProbe;

internal static class LobbyDataProbe
{
    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, ".."));
        var output = Path.Combine(root, "results", "lobby-data-tests");
        Directory.CreateDirectory(output);
        try
        {
            dwd.core.data.ReflectionTypeInitializer.Initialize();
            var source = File.ReadAllText(Path.Combine(root, "native-setup.json"));
            CheckOptions(source);
            var lobby = new PendingLobby(source);
            var host = Request(lobby, 0, "join");
            var account = new GameObject("Private test account").AddComponent<dwd.core.account.AccountProvider>();
            account.gameObject.tag = dwd.core.Finder.TAG;
            var store = new GameObject("Private test store").AddComponent<tuber.iap.TuberIAPStoreBehaviour>();
            store.gameObject.tag = dwd.core.Finder.TAG;
            store._Data_k__BackingField = new Il2CppSystem.Collections.Generic.Dictionary<string, dwd.iap.store.IAPProduct>()
                .Cast<Il2CppSystem.Collections.Generic.IReadOnlyDictionary<string, dwd.iap.store.IAPProduct>>();
            dwd.core.Finder.ClearCache();
            void Identity(JsonElement response)
            {
                var id = new AccountID(response.GetProperty("account").GetString());
                account.SetFactory(new lotus.data.account.LotusAccountProvider().Cast<dwd.core.account.IAccountFactory>());
                account.Initialize(new dwd.core.account.SerializableAccount { AccountID = id, Username = "Fixture", Attributes = new() });
                NativeLobbyData.UseIdentity(id);
            }
            Identity(host);
            var data = new PendingGameData(Metadata(host));
            Check(data.PlayerData.Count == 6, "Waiting room must contain six native entries");
            var actualFactions = Enumerable.Range(2, 4).Select(index => data.PlayerData[index].GetOne<FactionData>().ToString()).ToArray();
            Check(actualFactions.SequenceEqual(new[] { Factions.WoodlandAlliance, Factions.Vagabond, Factions.LizardCult, Factions.RiverfolkCompany }.Select(FactionText)), $"Native AI factions changed: {string.Join(',', actualFactions)}");
            var guest = Request(lobby, 1, "join");
            Identity(guest);
            var guestData = new PendingGameData(Metadata(guest));
            Check(!guestData.PlayerData[1].GetOne<PlayerSlotLockData>().FactionLocked, "Joining friend must be able to choose a faction");
            Request(lobby, 1, "lobby-join", "EyrieDynasties");
            guestData.UpdateWith(Metadata(JsonSerializer.SerializeToElement(lobby.View(1))));
            Check(guestData.PlayerData[1].GetOne<FactionData>().ToString() == FactionText(Factions.EyrieDynasties), "Joined faction did not update");
            Check(guestData.PlayerData[1].GetOne<PlayerSlotLockData>().SlotFilled, "Confirmed friend remains an open slot");

            var soloRequest = JSON.Deserialize<CreateLobbyGame>(source);
            soloRequest.NumberOfPlayers = 1;
            soloRequest.NumberOfAIPlayers = 5;
            var extraBot = new tuber.canis.data.matchinitdata.TuberPlayerMatchInitData(1, new AccountID(Guid.NewGuid().ToString()), "AI") { Faction = Factions.EyrieDynasties };
            tuber.canis.PlayerMatchInitDataPacker.PackPlayerMatchInitDatum("AIPlayerMetadata.4", extraBot, soloRequest.Options);
            var solo = new PendingLobby(JSON.ToJSON(soloRequest, false));
            var soloHost = Request(solo, 0, "join");
            Identity(soloHost);
            var soloData = new PendingGameData(Metadata(soloHost));
            Check(soloData.PlayerData.Count == 6 && soloData.PlayerData[5].GetOne<FactionData>().ToString() == FactionText(Factions.EyrieDynasties), "One-human room lost its fifth AI faction");
            Check(soloHost.GetProperty("canStart").GetBoolean(), "One human and five AI must be ready to start");

            var sixRequest = JSON.Deserialize<CreateLobbyGame>(source);
            sixRequest.NumberOfPlayers = 6;
            sixRequest.NumberOfAIPlayers = 0;
            for (var index = 0; index < 4; index++) sixRequest.Options.Remove($"AIPlayerMetadata.{index}");
            var six = new PendingLobby(JSON.ToJSON(sixRequest, false));
            Request(six, 0, "join");
            var factions = new[] { "MarquiseDeCat", "EyrieDynasties", "WoodlandAlliance", "Vagabond", "LizardCult", "RiverfolkCompany" };
            for (var seat = 1; seat < 4; seat++) { Request(six, seat, "join"); Request(six, seat, "lobby-join", factions[seat]); }
            var last = Request(six, 5, "join");
            Identity(last);
            var lastData = new PendingGameData(Metadata(last));
            Check(lastData.PlayerData.Count == 6, "Six-human waiting room must contain six entries");
            var localId = last.GetProperty("account").GetString();
            int[] LocalSlots() => Enumerable.Range(0, 6).Where(index => lastData.PlayerData[index].GetOne<lib.src.data.PendingGamePlayerAccountData>().PlayerAccountID?.ToString() == localId).ToArray();
            Check(LocalSlots().SequenceEqual(new[] { 4 }), "Friend must have exactly one editable slot across both native projections");
            lastData.PlayerData[4].GetOne<FactionData>().Faction = new Il2CppSystem.Nullable<Factions>(Factions.RiverfolkCompany);
            Request(six, 4, "join"); Request(six, 4, "lobby-join", factions[4]);
            lastData.UpdateWith(Metadata(JsonSerializer.SerializeToElement(six.View(5))));
            Check(LocalSlots().SequenceEqual(new[] { 5 }), "Sixth friend's native slot did not follow its identity");
            Check(lastData.PlayerData[5].GetOne<FactionData>().ToString() == FactionText(Factions.RiverfolkCompany), $"Roster update discarded the friend's in-progress faction choice: {lastData.PlayerData[5].GetOne<FactionData>()}; expected {FactionText(Factions.RiverfolkCompany)}");
            Check(!lastData.PlayerData[5].GetOne<PlayerSlotLockData>().FactionLocked, "Sixth friend's faction picker is locked");
            Request(six, 5, "lobby-join", factions[5]);
            lastData.UpdateWith(Metadata(JsonSerializer.SerializeToElement(six.View(5))));
            Request(six, 5, "lobby-leave");
            lastData.UpdateWith(Metadata(JsonSerializer.SerializeToElement(six.View(5))));
            Check(lastData.PlayerData[5].GetOne<FactionData>().ToString() == FactionText(Factions.RiverfolkCompany), "Leaving discarded the local faction selection");
            Check(JsonSerializer.SerializeToElement(six.View(0)).GetProperty("setup").GetProperty("Factions")[5].GetInt32() == 4, "Leaving must release the server faction reservation");
            File.WriteAllText(Path.Combine(output, "passed"), "PASS: six native lobby entries, AI factions, guest faction choice, join updates, and sixth-seat identity/selection preservation.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString()); }
        finally { NativeLobbyData.UseIdentity(null); }
        Application.Quit();
    }
    // Read through the native formatter: generated Nullable<enum> value accessors
    // do not marshal this value type correctly in this build.
    private static string FactionText(Factions faction) => new FactionData(new Il2CppSystem.Nullable<Factions>(faction)).ToString();
    private static void CheckOptions(string source)
    {
        var request = JSON.Deserialize<CreateLobbyGame>(source);
        var init = JSON.Deserialize<tuber.canis.TuberMatchInitData>(request.MatchInitData);
        init.AdsetDisableDraft = true;
        init.EnableBluff = false;
        init.Hirelings = true;
        init.NumLandmarks = 2;
        request.Options["AdvancedSetup"] = "True";
        request.Options["CooperativeMode"] = "True";
        request.Options["RandomSuits"] = "True";
        NativeOnlineConfiguration.Apply(request, init);
        Check(init.ChosenMap == MapLayout.Lake && init.ChosenDeck == DeckOptions.ExilesAndPartisans, "Online map and deck were not projected into native initialization");
        Check(init.AdvancedSetup && init.CooperativeMode, "Online advanced setup or co-op was lost");
        Check(init.ValueForOption("RandomSuits", "false") == "true", "Random clearing suits were not normalized for the native engine");
        Check(init.AdsetDisableDraft && !init.EnableBluff && init.Hirelings && init.NumLandmarks == 2, "Online conversion overwrote settings already in native initialization");
        foreach (var entry in new[] { ("ChosenMap", "999"), ("ChosenDeck", "unknown"), ("AdvancedSetup", "perhaps"), ("CooperativeMode", "1"), ("RandomSuits", "invalid") })
        {
            var original = request.Options[entry.Item1];
            request.Options[entry.Item1] = entry.Item2;
            var rejected = false;
            try { NativeOnlineConfiguration.Apply(request, init); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, $"Invalid online {entry.Item1} was accepted");
            request.Options[entry.Item1] = original;
        }
    }
    private static JsonElement Request(PendingLobby lobby, int seat, string op, string faction = "Invalid")
    {
        var result = JsonSerializer.SerializeToElement(lobby.Handle(seat, op, JsonSerializer.SerializeToElement(new { metadata = new Dictionary<string, string> { ["Faction"] = faction } })));
        Check(result.GetProperty("ok").GetBoolean(), $"Lobby fixture operation {op} for seat {seat + 1} was rejected");
        return result;
    }
    private static DWDPendingGameMetadata Metadata(JsonElement response) => JSON.Deserialize<DWDPendingGameMetadata>(response.GetProperty("pendingGame").GetRawText());
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
