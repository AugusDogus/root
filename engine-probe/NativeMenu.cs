using TMPro;
using UnityEngine;
using UnityEngine.UI;
using tuber.client.menus.prompts;
using Object = UnityEngine.Object;

namespace RootEngineProbe;

// Use sprites, fonts and button colours from the installed game's landing menu.
// No game artwork is shipped with the mod.
internal sealed class NativeMenu
{
    private readonly string lab;
    private readonly Action<string?, MatchSetup> startHost;
    private readonly NativeSetupMenu setupMenu;
    private MatchSetup hostedSetup = new();
    private readonly Func<(ulong Id, string Name)[]> friends;
    private readonly Func<int, ulong, string> invite;
    private GameObject? root;
    private GameObject? page;
    private Sprite? paper;
    private TMP_FontAsset? font;
    private ColorBlock colors;
    private bool playing;
    private bool hosting;
    private float nextFind;
    private string? failure;
    private NativeMatchMenu? matchMenu;
    public string Screen { get; private set; } = "";
    public bool IsPlaying => playing;
    public void AttachMatch(PrivateClient client, Func<SeatStatus[]> roster, Func<int, bool>? release, Action returnHome)
        => matchMenu = new(this, client, roster, release, returnHome);
    public void ReturnToBoard() => Toolbar();
    public void InviteSeat(int seat) => Friends(seat, friends(), 0);

    public NativeMenu(string lab, Action<string?, MatchSetup> startHost, Func<(ulong, string)[]> friends, Func<int, ulong, string> invite)
    {
        this.lab = lab; this.startHost = startHost; this.friends = friends; this.invite = invite;
        setupMenu = new(this, setup => Host(null, setup), Home);
    }
    public void SetHostedSetup(MatchSetup setup) => hostedSetup = setup;

    public void Update(float now)
    {
        matchMenu?.Update(now);
        if (root != null || now < nextFind) return;
        nextFind = now + 1;
        var landing = Object.FindObjectOfType<LandingPromptBehaviour>();
        if (landing == null) return;
        var source = landing.navigationButtons.FirstOrDefault(button => button.GetComponentInChildren<TMP_Text>() != null);
        if (source == null) return;
        var label = source.GetComponentInChildren<TMP_Text>();
        var image = source.targetGraphic.TryCast<Image>() ?? source.GetComponentInChildren<Image>();
        if (image == null || label == null) throw new InvalidOperationException("Root's menu artwork could not be loaded. Close Root and reopen the launcher.");
        paper = image.sprite;
        font = label.font;
        colors = source.colors;
        foreach (var button in landing.GetComponentsInChildren<Button>(true))
            if (!button.name.Contains("Settings", StringComparison.Ordinal)) button.gameObject.SetActive(false);
        root = new GameObject("Root Six Player Menu");
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 800);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        Home();
        if (failure is { } error) Error(error);
        File.WriteAllText(Path.Combine(lab, "native-menu-ready"), "ready");
    }

    public void Home()
    {
        Begin(false, "home");
        Text("Six Player", 28, 290, 340, 42, 30);
        Text("Private games with Steam friends", 28, 329, 390, 30, 19);
        Button("Host a game", 28, 380, 340, 66, setupMenu.Show);
        Button("Join friends", 28, 467, 340, 66, Waiting);
        Button("Resume a game", 28, 554, 340, 66, Saves);
        Button("Quit game", 28, 696, 290, 62, Application.Quit);
        Text("Version 0.2.0 · Friends' playtest", 28, 635, 410, 32, 18);
        Button("Get updates", 930, 696, 300, 62, () => Application.OpenURL("https://github.com/AugusDogus/root-six-player/releases"));
    }

    private void Host(string? save, MatchSetup? setup = null)
    {
        Connecting("Starting your game…", "Invite your friends once the board opens.");
        startHost(save, setup ?? new MatchSetup());
    }

    private void Waiting()
    {
        Begin(true, "join");
        Text("Join your friends", 300, 170, 680, 60, 38);
        Text("Accept your host's invitation in Steam.\nKeep this game open while you wait.", 300, 270, 680, 120, 25);
        Button("Back", 460, 500, 360, 62, Home);
    }

    private void Saves()
    {
        var directory = Path.GetFullPath(Path.Combine(lab, "..", "saves"));
        var saves = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderByDescending(Path.GetFileName).ToArray() : Array.Empty<string>();
        SavePage(saves, 0);
    }

    private void SavePage(string[] saves, int offset)
    {
        Begin(true, "saves");
        Text("Resume a game", 300, 100, 680, 60, 38);
        if (saves.Length == 0) Text("Your hosted matches will appear here.", 300, 250, 680, 100, 25);
        for (var index = offset; index < Math.Min(offset + 5, saves.Length); index++)
        {
            var name = Path.GetFileName(saves[index]);
            var display = Path.GetFileNameWithoutExtension(name);
            if (DateTime.TryParseExact(display.Length >= 19 ? display[..19] : display,
                    "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                display = date.ToString("MMM d, yyyy  h:mm tt");
            Button(display, 380, 190 + (index - offset) * 72, 520, 60, () => Host(name));
        }
        Paging(offset, saves.Length, next => SavePage(saves, next));
        Button("Back", 460, 675, 360, 60, Home);
    }

    public void Connecting(string title, string detail)
    {
        if (root == null) return;
        Begin(true, "connecting");
        Text(title, 260, 250, 760, 75, 38);
        Text(detail, 280, 345, 720, 110, 24);
    }

    public void Playing(bool isHost)
    {
        if (playing) return;
        // Root keeps this curtain's raycast blocker active during board loading.
        // Wait for it to close before exposing interactive match controls.
        if (GameObject.Find("P_ui_PlaymatLoadingCurtain(Clone)") != null) return;
        playing = true;
        hosting = isHost;
        Toolbar();
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") != "1")
            File.WriteAllText(Path.Combine(lab, "native-board-settings.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                map = (int)tuber.client.match.commands.LoadBoard.CurrentlyLoadedBoardMapLayout(),
                setup = hostedSetup
            }));
    }

    private void Toolbar()
    {
        Begin(false, "playing");
        if (hosting) Button("Invite friends", 215, 10, 185, 44, Seats);
        if (matchMenu is { } controls) Button("Match", 415, 10, 165, 44, controls.Show);
    }

    private void Seats()
    {
        Begin(true, "seats");
        Text("Invite Steam friends", 300, 65, 680, 65, 38);
        Text("Choose a faction for your friend", 300, 125, 680, 45, 23);
        for (var index = 0; index < 5; index++)
        {
            var seat = index + 2;
            if (MatchSetup.IsBot(hostedSetup.Factions[seat - 1])) continue;
            Button(MatchSetup.FactionName(hostedSetup.Factions[seat - 1]), 380, 195 + index * 72, 520, 60, () => Friends(seat, friends(), 0));
        }
        Button("Back to game", 460, 675, 360, 60, Toolbar);
    }

    private void Friends(int seat, (ulong Id, string Name)[] people, int offset)
    {
        Begin(true, "friends");
        Text($"Invite as {MatchSetup.FactionName(hostedSetup.Factions[seat - 1])}", 300, 70, 680, 65, 36);
        Text("Your friend needs the six-player game open.", 300, 135, 680, 50, 21);
        if (people.Length == 0) Text("No Steam friends found. Check Steam and try again.", 300, 240, 680, 110, 25);
        for (var index = offset; index < Math.Min(offset + 5, people.Length); index++)
        {
            var person = people[index];
            Button(person.Name, 380, 200 + (index - offset) * 70, 520, 60, () => InvitationResult(invite(seat, person.Id)));
        }
        Paging(offset, people.Length, next => Friends(seat, people, next));
        Button("Back", 460, 675, 360, 60, Seats);
    }

    private void InvitationResult(string result)
    {
        Begin(true, "invitation-result");
        Text(result, 280, 240, 720, 180, 28);
        Button("Invite another friend", 420, 475, 440, 65, Seats);
        Button("Back to game", 460, 580, 360, 60, Toolbar);
    }

    public void Notice(string title, string message)
    {
        Begin(true, "notice");
        Text(title, 260, 130, 760, 70, 34);
        Text(message, 200, 230, 880, 260, 24);
        Button(playing ? "Back to game" : "Back to menu", 460, 600, 360, 62, playing ? Toolbar : Home);
    }

    public void Error(string message, Action? reconnect = null, Action? returnHome = null)
    {
        failure = message;
        if (root == null) return;
        Begin(true, "error");
        Text("Could not continue", 260, 140, 760, 70, 36);
        Text(message, 260, 230, 760, 280, 24);
        if (reconnect is not null) Button("Reconnect", 460, 460, 360, 62, reconnect);
        if (returnHome is not null) Button("Return to menu", 460, 555, 360, 62, returnHome);
        Button("Quit game", 460, 650, 360, 62, Application.Quit);
    }

    private void Paging(int offset, int count, Action<int> select)
    {
        if (offset > 0) Button("Previous", 380, 575, 245, 55, () => select(offset - 5));
        if (offset + 5 < count) Button("Next", 655, 575, 245, 55, () => select(offset + 5));
    }

    internal void Begin(bool dim, string screen)
    {
        Screen = screen;
        if (Environment.GetEnvironmentVariable("ROOT_FRIENDS_LAUNCHER") != "1")
            File.WriteAllText(Path.Combine(lab, "native-menu-screen"), screen);
        if (page != null) { page.SetActive(false); Object.Destroy(page); }
        if (root == null) return;
        page = new GameObject("Six Player Controls");
        var rect = page.AddComponent<RectTransform>();
        rect.SetParent(root.transform, false);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        if (dim)
        {
            var shade = page.AddComponent<Image>();
            shade.color = new Color(0.08f, 0.07f, 0.04f, 0.93f);
        }
    }

    private GameObject Element(string name, float x, float y, float width, float height)
    {
        if (page == null) throw new InvalidOperationException("Menu controls are not ready.");
        var item = new GameObject(name);
        var rect = item.AddComponent<RectTransform>();
        rect.SetParent(page.transform, false);
        rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return item;
    }

    internal TMP_Text Text(string value, float x, float y, float width, float height, float size)
    {
        var label = Element("Label", x, y, width, height).AddComponent<TextMeshProUGUI>();
        label.font = font;
        label.text = value;
        label.richText = false;
        label.fontSize = size;
        label.color = new Color(1, 0.97f, 0.87f, 1);
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        return label;
    }

    internal void Button(string title, float x, float y, float width, float height, Action action)
    {
        var item = Element(title, x, y, width, height);
        var image = item.AddComponent<Image>();
        image.sprite = paper;
        image.type = Image.Type.Sliced;
        var button = item.AddComponent<Button>();
        button.targetGraphic = image;
        button.colors = colors;
        button.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
        {
            try { action(); }
            catch (Exception error) { Error(error.Message); }
        }));
        var text = Text(title, x + 16, y, width - 32, height, Math.Min(32, height / 2));
        text.enableAutoSizing = true;
        text.fontSizeMin = 18;
        text.fontSizeMax = Math.Min(32, height / 2);
    }

    public void Destroy() { if (root != null) Object.Destroy(root); }
}
