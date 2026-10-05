namespace EchoFlow;

/// The app's window: Home, Dictionary, History and Settings. Closing it just hides it to the tray.
sealed class MainForm : Form
{
    readonly TrayApp app;
    readonly Icon icon;
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly List<(NavItem Nav, Control Page)> pages = [];
    readonly StatusDot sideStatus = new();

    // Home
    readonly StatTile dictations = new("Dictations this month"), words = new("Words this month"), cost = new("AI cost this month");
    readonly Note homeHint = new("");
    readonly Card statusCard = new();
    readonly StatusDot aiDot = new();
    Card.Row modelRow = null!;

    // Dictionary
    readonly Field wordsField = new(multiline: true);
    readonly Label wordsSaved = new() { AutoSize = true, Tag = "muted" };

    // History
    readonly HistoryList historyList = new() { Dock = DockStyle.Fill };
    readonly Label historyHint = new() { AutoSize = true, Tag = "muted" };

    // Settings
    readonly Card dictationCard = new(), aiCard = new();
    Card.Row shortcutsRow = null!, micRow = null!, keyRow = null!;
    readonly ComboBox micBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(190) };
    readonly FlatButton testButton = new("Test");
    readonly LevelBar meter = new() { Width = Ui.S(60) };
    readonly Recorder testRecorder = new();
    readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 50 };
    readonly Field keyField = new() { Width = Ui.S(220) };
    string? keyMessage;

    public MainForm(TrayApp app, Icon icon)
    {
        this.app = app;
        this.icon = icon;
        AutoScaleMode = AutoScaleMode.None; // sizes are scaled by hand with Ui.S
        Text = "EchoFlow";
        Icon = icon;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Ui.S(940), Ui.S(660));
        MinimumSize = new Size(Ui.S(780), Ui.S(540));

        AddPage("Home", Ui.HomeIcon, BuildHome());
        AddPage("Dictionary", Ui.BookIcon, BuildDictionary());
        AddPage("History", Ui.HistoryIcon, BuildHistory());
        AddPage("Settings", Ui.SettingsIcon, BuildSettings());
        Controls.Add(content); // the fill goes first so the sidebar docks beside it
        Controls.Add(BuildSidebar());
        ShowPage(0);

        Theme.Changed += OnThemeChanged;
        Theme.Apply(this);
        RefreshStatus();

        meterTimer.Tick += (_, _) => meter.Level = testRecorder.Level;
        Activated += (_, _) => { RefreshStatus(); OnPageShown(); };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; // keep running in the tray
            StopTest();
            Hide();
        };
    }

    void OnThemeChanged()
    {
        Theme.Apply(this);
        RefreshStatus();
    }

    // ---- Sidebar ----

    void AddPage(string title, string iconGlyph, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        content.Controls.Add(page);
        var nav = new NavItem(title, iconGlyph);
        int index = pages.Count;
        nav.Click += (_, _) => ShowPage(index);
        pages.Add((nav, page));
    }

    Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = Ui.S(210), Tag = "sidebar", BackColor = Theme.Sidebar };
        sidebar.Controls.Add(new Label
        {
            Text = "EchoFlow",
            Font = Theme.Heading,
            AutoSize = true,
            Location = new Point(Ui.S(22), Ui.S(22)),
        });
        int y = Ui.S(82);
        foreach (var (nav, _) in pages)
        {
            nav.Bounds = new Rectangle(Ui.S(12), y, sidebar.Width - Ui.S(24), nav.Height);
            sidebar.Controls.Add(nav);
            y += nav.Height + Ui.S(4);
        }
        sidebar.Controls.Add(sideStatus);
        sidebar.Resize += (_, _) => sideStatus.Location = new Point(Ui.S(24), sidebar.Height - Ui.S(46));
        return sidebar;
    }

    void ShowPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Nav.Selected = i == index;
            pages[i].Page.Visible = i == index;
        }
        OnPageShown();
    }

    Control? CurrentPage => pages.FirstOrDefault(p => p.Page.Visible).Page;

    void OnPageShown()
    {
        var page = CurrentPage;
        if (page != pages[3].Page) StopTest();
        if (page == pages[1].Page && !wordsField.Box.Modified) LoadWords();
        if (page == pages[2].Page) { historyList.Reload(app.History); historyHint.Text = ""; }
    }

    // ---- Home ----

    Control BuildHome()
    {
        var page = new Page();
        page.Heading("Welcome back");
        page.Add(homeHint, 20);

        var tiles = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = dictations.Height };
        for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        foreach (var tile in new[] { dictations, words, cost })
        {
            tile.Dock = DockStyle.Fill;
            tile.Margin = new Padding(0, 0, tile == cost ? 0 : Ui.S(12), 0);
            tiles.Controls.Add(tile);
        }
        page.Add(tiles, 28);

        page.Section("Status");
        modelRow = statusCard.AddRow("Speech model", app.Status);
        statusCard.AddRow("AI cleanup", "Colorful bars on the dictation pill mean AI cleanup is working; white bars mean it isn't.", aiDot);
        page.Add(statusCard);
        return page;
    }

    // ---- Dictionary ----

    Control BuildDictionary()
    {
        var page = new Page { AutoScroll = false };
        page.Heading("Dictionary");
        page.Note("One word or name per line and it's always spelled that way. Use \"heard -> wanted\" to fix " +
                  "something that keeps coming out wrong, e.g.  chat gpt -> ChatGPT");

        var save = new FlatButton("Save", primary: true);
        save.Click += (_, _) => SaveWords();
        var bar = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(56) };
        bar.Controls.Add(save);
        bar.Controls.Add(wordsSaved);
        save.Location = new Point(0, Ui.S(16));
        wordsSaved.Location = new Point(save.Right + Ui.S(14), Ui.S(23));
        page.Controls.Add(bar);

        var box = wordsField.Box;
        box.AcceptsReturn = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.Font = new Font("Segoe UI", 10.5f);
        box.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.S) { SaveWords(); e.SuppressKeyPress = true; } };
        box.TextChanged += (_, _) => wordsSaved.Text = "";
        wordsField.Dock = DockStyle.Fill;
        page.Controls.Add(wordsField);
        wordsField.BringToFront(); // docks last, filling what's left
        return page;
    }

    void LoadWords()
    {
        var box = wordsField.Box;
        // The text box only breaks lines on \r\n.
        box.Text = File.Exists(app.Words.FilePath) ? File.ReadAllText(app.Words.FilePath).ReplaceLineEndings("\r\n") : "";
        box.SelectionStart = box.TextLength;
        box.Modified = false;
        wordsSaved.Text = "";
    }

    void SaveWords()
    {
        try
        {
            File.WriteAllText(app.Words.FilePath, wordsField.Box.Text);
            wordsField.Box.Modified = false;
            wordsSaved.Text = "Saved. Used from your next dictation.";
        }
        catch (IOException ex)
        {
            wordsSaved.Text = $"Couldn't save: {ex.Message}";
        }
    }

    // ---- History ----

    Control BuildHistory()
    {
        var page = new Page { AutoScroll = false };
        page.Heading("History");
        page.Note("Everything you've dictated, newest first. Double-click one to copy it.");

        var copy = new FlatButton("Copy", primary: true);
        copy.Click += (_, _) => CopySelected();
        var bar = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(56) };
        bar.Controls.Add(copy);
        bar.Controls.Add(historyHint);
        copy.Location = new Point(0, Ui.S(16));
        historyHint.Location = new Point(copy.Right + Ui.S(14), Ui.S(23));
        page.Controls.Add(bar);

        historyList.DoubleClick += (_, _) => CopySelected();
        historyList.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.C) CopySelected(); };
        page.Controls.Add(historyList);
        historyList.BringToFront();
        return page;
    }

    void CopySelected()
    {
        if (historyList.SelectedText is not { } text) return;
        Clipboard.SetText(text);
        historyHint.Text = "Copied.";
    }

    // ---- Settings ----

    Control BuildSettings()
    {
        var page = new Page();
        page.Heading("Settings");
        page.Add(new Panel { Height = Ui.S(6) }, 0);

        // Dictation
        page.Section("Dictation");
        var change = new FlatButton("Change");
        change.Click += (_, _) =>
        {
            using (var form = new ShortcutsForm(app, icon)) form.ShowDialog(this);
            RefreshStatus();
        };
        shortcutsRow = dictationCard.AddRow("Shortcuts", null, change);

        micBox.Items.Add("Windows default");
        micBox.Items.AddRange(Recorder.Devices().ToArray());
        micBox.SelectedIndex = Math.Max(0, micBox.Items.IndexOf(app.Settings.Microphone));
        micBox.SelectedIndexChanged += (_, _) =>
        {
            app.Settings.Microphone = micBox.SelectedIndex == 0 ? "" : (string)micBox.SelectedItem!;
            app.Settings.Save();
            app.ApplyMicrophone();
            if (testRecorder.IsRecording) { StopTest(); StartTest(); }
        };
        testButton.Click += (_, _) => { if (testRecorder.IsRecording) StopTest(); else StartTest(); };
        micRow = dictationCard.AddRow("Microphone", "Press Test and talk to check it's hearing you", new Stack(false, 10, micBox, testButton, meter));
        page.Add(dictationCard, 26);

        // AI cleanup
        page.Section("AI cleanup");
        aiCard.AddRow("AI cleanup", "Removes filler words, fixes punctuation and formats lists, using Claude",
            Switch(app.Settings.Cleanup, on => { app.Settings.Cleanup = on; app.UpdateAiStatus(); }));

        keyField.Box.UseSystemPasswordChar = true;
        var saveKey = new FlatButton("Save");
        async void SaveKey()
        {
            if (!saveKey.Enabled) return;
            saveKey.Enabled = false;
            saveKey.Text = "Checking…";
            keyMessage = await app.SaveKeyAsync(keyField.Box.Text);
            keyField.Box.Text = "";
            saveKey.Text = "Save";
            saveKey.Enabled = true;
            RefreshStatus();
        }
        saveKey.Click += (_, _) => SaveKey();
        keyField.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SaveKey(); e.SuppressKeyPress = true; } };
        keyRow = aiCard.AddRow("API key", null, new Stack(false, 8, keyField, saveKey));

        aiCard.AddRow("Anthropic account", "Get a key, and see what you've used and spent", new Stack(false, 8,
            Link("Get key", "https://console.anthropic.com/settings/keys"),
            Link("Usage", "https://console.anthropic.com/settings/usage"),
            Link("Billing", "https://console.anthropic.com/settings/billing")));
        aiCard.AddRow("Claude plan usage", "Your claude.ai subscription limits (separate from the API)",
            Link("Open", "https://claude.ai/settings/usage"));
        page.Add(aiCard, 26);

        // App
        page.Section("App");
        var appCard = page.Add(new Card(), 26);
        appCard.AddRow("Launch app at login", null, Switch(TrayApp.StartsWithWindows(), TrayApp.SetStartWithWindows));
        appCard.AddRow("Show bar at all times", "Keep a small bar at the bottom of the screen",
            Switch(app.Settings.ShowBarAlways, on => { app.Settings.ShowBarAlways = on; app.ApplyBar(); }));
        appCard.AddRow("Dark mode", null, Switch(app.Settings.DarkMode, on => { app.Settings.DarkMode = on; Theme.Set(on); }));

        // Sound
        page.Section("Sound");
        var soundCard = page.Add(new Card(), 26);
        soundCard.AddRow("Dictation sounds", "A soft chime when dictation starts and stops",
            Switch(app.Settings.Sounds, on => app.Settings.Sounds = on));
        soundCard.AddRow("Pause other audio while dictating", "Pauses music and videos while you talk and resumes them when you let go",
            Switch(app.Settings.QuietWhileDictating, on => app.Settings.QuietWhileDictating = on));

        // Extras
        page.Section("Extras");
        var extrasCard = page.Add(new Card(), 26);
        extrasCard.AddRow("Auto-add to dictionary", "When you fix a word right after dictating, it's added to your dictionary",
            Switch(app.Settings.AutoAddWords, on => app.Settings.AutoAddWords = on));
        extrasCard.AddRow("Your files", "Settings, history, dictionary and log", new Stack(false, 8,
            Link("Data folder", Settings.AppDataDir),
            Link("Log", Log.FilePath)));
        return page;
    }

    Toggle Switch(bool on, Action<bool> set)
    {
        var toggle = new Toggle(on);
        toggle.Toggled += value =>
        {
            set(value);
            app.Settings.Save();
        };
        return toggle;
    }

    static FlatButton Link(string text, string target)
    {
        var button = new FlatButton(text + "  ↗");
        button.Click += (_, _) => Ui.Open(target);
        return button;
    }

    void StartTest()
    {
        try
        {
            testRecorder.Microphone = app.Settings.Microphone;
            testRecorder.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Mic test failed: {ex.Message}");
            dictationCard.SetSubtitle(micRow, "Couldn't open that microphone");
            return;
        }
        dictationCard.SetSubtitle(micRow, "Talk now. The bar should move when you speak");
        testButton.Text = "Stop";
        meterTimer.Start();
    }

    void StopTest()
    {
        if (!testRecorder.IsRecording) return;
        meterTimer.Stop();
        testRecorder.Stop();
        testButton.Text = "Test";
        meter.Level = 0;
        dictationCard.SetSubtitle(micRow, "Press Test and talk to check it's hearing you");
    }

    // ----

    public void RefreshStatus()
    {
        if (IsDisposed) return;
        var month = app.History.ThisMonth();
        dictations.Value = month.Dictations.ToString("N0");
        words.Value = month.Words.ToString("N0");
        cost.Value = $"${month.EstimatedCost:0.00}";

        string keys = HoldHotkey.Display(app.Settings.Hotkey);
        homeHint.Text = $"Hold {keys} to talk. Double-tap it to go hands-free, then tap again to finish. Esc cancels.";
        string handsFree = app.Settings.HandsFreeHotkey.Length > 0 ? $" or press {HoldHotkey.Display(app.Settings.HandsFreeHotkey)}" : "";
        dictationCard.SetSubtitle(shortcutsRow, $"Hold {keys} to talk. Double-tap{handsFree} for hands-free.");

        statusCard.SetSubtitle(modelRow, app.Status);
        var color = !app.Settings.Cleanup ? Theme.Off : app.AiActive ? Theme.Good : Theme.Warn;
        aiDot.Set(!app.Settings.Cleanup ? "Off" : !app.Cleaner.HasKey ? "Needs an API key"
            : app.AiError is { } error ? $"Not working: {error}" : "Working", color);
        sideStatus.Set(!app.Settings.Cleanup ? "AI off" : app.AiActive ? "AI on" : "AI needs attention", color);

        aiCard.SetSubtitle(keyRow, keyMessage
            ?? (!app.Cleaner.HasKey ? "Paste your key from the Anthropic console"
            : app.AiError is { } keyError ? $"Not working: {keyError}"
            : "Saved on this PC, encrypted"));
        keyField.Box.PlaceholderText = app.Cleaner.HasKey ? "Paste a new key to replace it" : "sk-ant-…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            meterTimer.Dispose();
            testRecorder.Dispose();
        }
        base.Dispose(disposing);
    }
}
