using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WP = System.Windows.Controls.Primitives;

namespace San14Tenkazu;

// The officer's editor card, in four pages: 기본·능력 (portrait, the ability pentagon, 충성, abilities, years,
// 공적, 성별, 주의, 정책 and its 등급, 관직, 상성), 개성·전법·진형, 관계·명품 (배우자, 의형제, 친애, 혐오, 명품) and 소속.
// The names of his force, his base and the officers he is tied to open their own cards.
internal sealed partial class OfficerEditorCard : EditorCard
{
    // Another officer as the card names him, for 배우자, 의형제, 친애 and 혐오.
    internal readonly record struct Other(int Id, string Label, int Gender);

    // A 명품 as the card offers it: whom it is with now (Holder, "" for none) and what it adds (Tip).
    internal readonly record struct Treasure(int Index, string Name, int Owner, string Holder, string Tip);

    // Where the officer stands: his force (0 none), his base (a city, gate or tribe; 0 none), his status and office;
    // for the [소속] page also his corps, where he is now, the ruler he left (each "" when none), why he cannot be
    // moved or let go now and why he cannot be sent to another force ("" when he can; a commander out with his unit can).
    internal sealed record Standing(int Force, string ForceName, System.Drawing.Color Colour, string Status, int Base, string BaseName, string Office,
        string Corps = "", string Place = "", string FormerLord = "", string Blocked = "", string SendBlocked = "");

    // The game's officers have up to five 개성; the slots after them are shown only when an officer uses them.
    private const int UsualPersonalities = 5;
    private const int MaxTies = 8, MaxTactics = 5;

    private readonly WC.Image portrait = new() { Width = 120, Height = 170, Stretch = WM.Stretch.UniformToFill, VerticalAlignment = W.VerticalAlignment.Top };
    private readonly AbilityChart chart = new() { MinWidth = 220, Height = 200, Margin = new W.Thickness(16, 0, 0, 0) };
    private readonly NumberField loyalty = new(OfficerFields.Loyalty, 60), birth = new(OfficerFields.Birth, 60), debut = new(OfficerFields.Debut, 60),
        death = new(OfficerFields.Death, 60), compatibility = new(OfficerFields.Compatibility, 60);
    private readonly NumberField merit = new(OfficerFields.Merit);
    private readonly NumberField[] abilities = OfficerFields.Abilities.Select(f => new NumberField(f, 52)).ToArray();
    // The AI's leanings (AiTraits): what the trees weigh for the force he rules or the corps he leads.
    private readonly NumberField[] aiTraits = AiTraits.Fields.Select(f => new NumberField(f, 52)).ToArray();
    private readonly NameChoice gender = new(64, none: false), ideology = new(96, none: false), policy = new(196), uniqueTactic = new(196),
        policyLevel = new(56, none: false), office = new(196, none: false);
    private readonly WC.TextBlock officeNote = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 320 };
    private IReadOnlyDictionary<int, string> officeHolders = new Dictionary<int, string>();
    private readonly NameChoice[] personalities = Enumerable.Range(0, OfficerFields.Personalities.Length).Select(_ => new NameChoice(190)).ToArray();
    private readonly WC.Border[] tacticChips = new WC.Border[OfficerFields.CommonTactics + 1];   // by tactic ID
    private readonly WC.TextBlock tacticNote = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 380, Margin = new W.Thickness(0, 2, 0, 0) };
    private readonly WC.TextBlock tacticCount = new() { Margin = new W.Thickness(0, 4, 0, 2) };
    // 진형: a chip for each he may be taught (OfficerFields.FormationsFor), dark when he has it.
    private readonly WC.WrapPanel formationPanel = new();
    private readonly Dictionary<int, WC.Border> formationChips = [];
    private long currentFormations, pickedFormations;
    private GameCatalog names = GameCatalog.Empty;
    private readonly WC.ComboBox spouse = OfficerChoice(), sworn = OfficerChoice();
    private readonly WC.TextBlock swornMembers = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 320, Margin = new W.Thickness(0, 0, 0, 2) };
    private readonly WC.DockPanel swornGroupLine;
    private readonly TieList likes, dislikes;
    private readonly WC.ComboBox give = OfficerChoice();
    private readonly WC.WrapPanel treasureChips = new();
    private readonly WC.Border treasureFrame = new() { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(1), Padding = new W.Thickness(3), MinHeight = 28 };
    private List<Treasure> treasures = [], offered = [];
    private readonly List<int> given = [];
    // [소속]: where he stands now, and the transfers his status allows.
    private readonly WC.TextBlock standingForce = Value(), standingCorps = Value(), standingBase = Value(), standingPlace = Value(),
        standingStatus = Value(), standingLord = Value();
    private readonly WC.TextBlock transferNote = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 380, Margin = new W.Thickness(0, 4, 0, 0) };
    private readonly WC.TextBlock transferHow = Hint(L.Officer.TransferNote);
    private readonly WC.TextBlock joinLabel = Label(L.Officer.OtherForce);
    private readonly WC.ComboBox moveBase = Choice(170), joinForce = Choice(170), joinBase = Choice(170);
    private readonly PlateButton moveButton = new(L.Officer.Move, icon: Icons.Details), joinButton = new(L.Officer.Send, icon: Icons.Details);
    private readonly WC.StackPanel moveLine = new(), joinLine = new();
    private List<Transfers.ForceBases> posts = [];
    private List<(int Base, string Name)> moveChoices = [];
    private IReadOnlyList<Transfers.ForceBases> forcePosts = [];
    private int status;

    private byte[] record = [];
    private int shownPersonalities = UsualPersonalities;
    private readonly HashSet<int> pickedTactics = [], currentTactics = [];
    // Tactics he learned beyond the common ones (in his slots' order): kept before the common ones unless forgotten.
    // They have chips of their own, above the common ones, so the five slots he has are all in sight.
    private List<int> keptTactics = [];
    private readonly HashSet<int> forgottenTactics = [];
    private readonly List<WC.Border> keptChips = [];
    private readonly WC.WrapPanel keptPanel = new();
    private GameName[] tacticNames = [];
    private List<int> spouseValues = [], otherValues = [];
    private int currentSpouse, currentSworn, pending;
    private Dictionary<int, string> labels = [];
    private bool filling;

    // The officer being edited (0 when none).
    internal int OfficerId { get; private set; }

    internal override EditorTarget? Target => OfficerId == 0 ? null : new OfficerTarget(OfficerId);

    internal override int Pending => pending;

    internal event Action<Transfer, int>? TransferClicked;

    internal OfficerEditorCard()
    {
        swornGroupLine = Line("", swornMembers);
        likes = new TieList(L.Officer.Likes, OfficerFields.Likes, OpenOfficer);
        dislikes = new TieList(L.Officer.Dislikes, OfficerFields.Dislikes, OpenOfficer);
        AddPage(L.Officer.BasicPage, BasicPage());
        AddPage(L.Officer.TraitPage, TraitPage());
        AddPage(L.Officer.TiesPage, TiesPage());
        AddPage(L.Officer.StandingPage, StandingPage());

        foreach (NumberField field in Numbers()) field.Edited += Mark;
        foreach (NameChoice choice in Choices(personalities.Length)) choice.Box.SelectionChanged += (_, _) => Mark();
        office.Box.SelectionChanged += (_, _) => NoteOffice();
        spouse.SelectionChanged += (_, _) => Mark();
        sworn.SelectionChanged += (_, _) => Mark();
        likes.Changed += Mark;
        dislikes.Changed += Mark;
    }

    private W.UIElement BasicPage()
    {
        var page = new WC.StackPanel();
        var top = new WC.Grid { Margin = new W.Thickness(0, 2, 0, 8) };
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = W.GridLength.Auto });
        top.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(1, W.GridUnitType.Star) });
        var frame = new WC.Border { BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(2), Background = CardSkin.DarkWood, Child = portrait, VerticalAlignment = W.VerticalAlignment.Top };
        WM.RenderOptions.SetBitmapScalingMode(portrait, WM.BitmapScalingMode.HighQuality);
        top.Children.Add(frame);
        WC.Grid.SetColumn(chart, 1);
        top.Children.Add(chart);
        page.Children.Add(top);
        page.Children.Add(Line(L.Officer.Loyalty, loyalty.Box, new WC.TextBlock { Text = OfficerFields.Loyalty.Hint, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center }));
        page.Children.Add(Line(OfficerFields.Merit.Label, merit.Box));
        var grid = new WP.UniformGrid { Columns = 5, Margin = new W.Thickness(0, 2, 0, 2) };
        for (int i = 0; i < abilities.Length; i++)
        {
            var cell = new WC.StackPanel();
            cell.Children.Add(new WC.TextBlock { Text = OfficerFields.Abilities[i].Label, Foreground = CardSkin.InkMuted });
            cell.Children.Add(abilities[i].Box);
            grid.Children.Add(cell);
        }
        page.Children.Add(grid);
        page.Children.Add(Line(L.Officer.Years, birth.Box, Label(L.Officer.Debut), debut.Box, Label(L.Officer.Death), death.Box));
        birth.Box.ToolTip = null;
        page.Children.Add(Heading(L.Officer.Traits));
        page.Children.Add(Line(L.Officer.Gender, gender.Box, Label(L.Officer.Ideology, 14), ideology.Box));
        page.Children.Add(Line(L.Officer.Policy, policy.Box, Label(L.Officer.PolicyLevel, 14), policyLevel.Box));
        page.Children.Add(Line(L.Officer.Office, office.Box));
        WC.DockPanel noteLine = Line("", officeNote);
        noteLine.SetBinding(W.UIElement.VisibilityProperty, new W.Data.Binding(nameof(W.UIElement.Visibility)) { Source = officeNote });
        page.Children.Add(noteLine);
        page.Children.Add(Line(L.Officer.Compatibility, compatibility.Box));
        policyLevel.Box.ToolTip = L.Officer.PolicyLevelTip;
        gender.Box.ToolTip = L.Officer.GenderTip;
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Officer.ExperienceNote, Foreground = CardSkin.InkMuted,
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        return page;
    }

    private W.UIElement TraitPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Officer.Personalities));
        var personalityGrid = new WP.UniformGrid { Columns = 2 };
        foreach (NameChoice choice in personalities)
        {
            choice.Box.Margin = new W.Thickness(0, 2, 6, 2);
            personalityGrid.Children.Add(choice.Box);
        }
        page.Children.Add(personalityGrid);
        page.Children.Add(Heading(L.Officer.Tactics));
        page.Children.Add(Line(L.Officer.UniqueTactic, uniqueTactic.Box));
        var tacticPanel = new WC.WrapPanel();
        for (int id = 1; id <= OfficerFields.CommonTactics; id++)
        {
            int tactic = id;
            tacticChips[id] = TacticChip(() => ToggleTactic(tactic));
            tacticPanel.Children.Add(tacticChips[id]);
        }
        keptPanel.Margin = new W.Thickness(0, 0, 0, 4);
        page.Children.Add(tacticCount);
        page.Children.Add(keptPanel);
        page.Children.Add(tacticPanel);
        page.Children.Add(tacticNote);
        page.Children.Add(Heading(L.Officer.Formations));
        page.Children.Add(formationPanel);
        page.Children.Add(Heading(L.Ai.Traits));
        var traitGrid = new WP.UniformGrid { Columns = 5, Margin = new W.Thickness(0, 2, 0, 2) };
        for (int i = 0; i < aiTraits.Length; i++)
        {
            var cell = new WC.StackPanel();
            cell.Children.Add(new WC.TextBlock { Text = AiTraits.Fields[i].Label, Foreground = CardSkin.InkMuted, ToolTip = AiTraits.Tips[i] });
            aiTraits[i].Box.ToolTip = AiTraits.Tips[i];
            cell.Children.Add(aiTraits[i].Box);
            traitGrid.Children.Add(cell);
        }
        page.Children.Add(traitGrid);
        page.Children.Add(new WC.TextBlock { Text = L.Ai.TraitsNote, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 2, 0, 0) });
        return page;
    }

    private static WC.Border TacticChip(Action click)
    {
        var chip = new WC.Border
        {
            CornerRadius = new W.CornerRadius(3), Padding = new W.Thickness(6, 2, 6, 2), Margin = new W.Thickness(0, 2, 4, 2),
            Cursor = WI.Cursors.Hand, MinWidth = 40, Child = new WC.TextBlock { TextAlignment = W.TextAlignment.Center }
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return chip;
    }

    // A learned tactic is dark; one that changes is framed in the highlight colour.
    private static void ShowChip(WC.Border chip, bool learned, bool was)
    {
        chip.Background = learned ? CardSkin.DarkWood : CardSkin.Paper;
        ((WC.TextBlock)chip.Child).Foreground = learned ? CardSkin.InkOnDark : CardSkin.Ink;
        chip.BorderBrush = learned != was ? CardSkin.Highlight : CardSkin.Rule;
        chip.BorderThickness = new W.Thickness(learned != was ? 2 : 1);
    }

    private W.UIElement TiesPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Officer.Ties));
        page.Children.Add(Line(L.Officer.Spouse, spouse));
        spouse.ToolTip = L.Officer.SpouseTip;
        page.Children.Add(Line(L.Officer.Sworn, sworn));
        sworn.ToolTip = L.Officer.SwornTip;
        page.Children.Add(swornGroupLine);
        page.Children.Add(likes.Panel);
        page.Children.Add(dislikes.Panel);
        page.Children.Add(Heading(L.Officer.Items));
        page.Children.Add(Line(L.Officer.Give, give));
        give.ToolTip = L.Officer.GiveTip;
        treasureFrame.Child = treasureChips;
        page.Children.Add(treasureFrame);
        give.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.Enter) return;
            e.Handled = true;
            Give();
        };
        give.DropDownClosed += (_, _) => Give();
        return page;
    }

    private W.UIElement StandingPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Officer.Now));
        page.Children.Add(Line(L.Officer.Force, standingForce));
        page.Children.Add(Line(L.Officer.Corps, standingCorps));
        page.Children.Add(Line(L.Officer.Base, standingBase));
        page.Children.Add(Line(L.Officer.Place, standingPlace));
        page.Children.Add(Line(L.Officer.Status, standingStatus));
        page.Children.Add(Line(L.Officer.FormerLord, standingLord));
        page.Children.Add(Heading(L.Officer.MoveHeading));
        moveLine.Children.Add(Line(L.Officer.SameForce, moveBase, moveButton));
        moveLine.Children.Add(Hint(L.Officer.MoveTip));
        var forceLine = Line(joinLabel, joinForce);
        joinLine.Children.Add(forceLine);
        joinLine.Children.Add(Line(L.Officer.TargetBase, joinBase, joinButton));
        joinLine.Margin = new W.Thickness(0, 8, 0, 0);
        moveButton.Margin = joinButton.Margin = new W.Thickness(6, 0, 0, 0);
        page.Children.Add(moveLine);
        page.Children.Add(joinLine);
        page.Children.Add(transferNote);
        transferHow.Margin = new W.Thickness(0, 8, 0, 0);
        page.Children.Add(transferHow);
        page.Children.Add(StatusChangePanel());
        page.Children.Add(RevivalPanel());
        joinForce.SelectionChanged += (_, _) => FillJoinBases();
        moveButton.Click += () => { if (moveBase.SelectedIndex >= 0) TransferClicked?.Invoke(Transfer.Move, moveChoices[moveBase.SelectedIndex].Base); };
        joinButton.Click += () =>
        {
            if (joinForce.SelectedIndex < 0 || joinBase.SelectedIndex < 0) return;
            TransferClicked?.Invoke(status is 5 or 6 ? Transfer.Recruit : Transfer.Defect, posts[joinForce.SelectedIndex].Bases[joinBase.SelectedIndex].Base);
        };
        return page;
    }

    private static WC.TextBlock Value() => new() { VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 320 };

    private static WC.TextBlock Hint(string text) => new() { Text = text, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 380 };

    private void FillJoinBases()
    {
        joinBase.ItemsSource = joinForce.SelectedIndex < 0 ? null : posts[joinForce.SelectedIndex].Bases.Select(b => b.Name).ToList();
        joinBase.SelectedIndex = joinForce.SelectedIndex < 0 ? -1 : 0;
    }

    // The [소속] page for the officer: his standing, and the transfers his status allows (the others hidden, with why).
    private void ShowTransfers(OfficerRecord officer, Standing standing, IReadOnlyList<Transfers.ForceBases> forces)
    {
        status = officer.Status;
        forcePosts = forces;
        bool serving = status is 2 or 3 or 4, free = status is 5 or 6;
        moveChoices = forces.FirstOrDefault(f => f.Force == standing.Force)?.Bases.Where(b => b.Base != standing.Base).ToList() ?? [];
        moveBase.ItemsSource = moveChoices.Select(b => b.Name).ToList();
        moveBase.SelectedIndex = moveChoices.Count > 0 ? 0 : -1;
        moveLine.Visibility = serving && moveChoices.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        // Another force to serve: a 재야 officer or a prisoner joins any force; one serving now leaves his own.
        posts = free ? [.. forces] : forces.Where(p => p.Force != standing.Force).ToList();
        joinForce.ItemsSource = posts.Select(p => p.Name).ToList();
        joinForce.SelectedIndex = posts.Count > 0 ? 0 : -1;
        FillJoinBases();
        joinLine.Visibility = (serving || free) && posts.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        joinLabel.Text = free ? L.Officer.RecruitingForce : L.Officer.OtherForce;
        joinButton.Text = free ? L.Officer.Recruit : L.Officer.Send;
        ShowNow(standing);
    }

    // The page's "지금" lines, and the buttons off (with why) while he cannot be moved: leading a unit (then only
    // [보내기] stays on), or out of his base on the way or on a task.
    private void ShowNow(Standing standing)
    {
        Fill(standingForce, [standing.Force > 0 ? Link(standing.ForceName, new ForceTarget(standing.Force)) : new WD.Run(standing.ForceName)]);
        standingCorps.Text = standing.Corps.Length > 0 ? standing.Corps : "-";
        Fill(standingBase, [BaseLink(standing) ?? new WD.Run(standing.BaseName.Length > 0 ? standing.BaseName : "-")]);
        standingPlace.Text = standing.Place.Length > 0 ? standing.Place : "-";
        standingStatus.Text = standing.Status;
        standingLord.Text = standing.FormerLord.Length > 0 ? standing.FormerLord : "-";

        bool blocked = standing.Blocked.Length > 0;
        transferHow.Visibility = status is 7 or 9 ? W.Visibility.Collapsed : W.Visibility.Visible;
        moveButton.IsEnabled = !blocked;
        joinButton.IsEnabled = standing.SendBlocked.Length == 0;
        transferNote.Foreground = blocked ? CardSkin.Poor : CardSkin.InkMuted;
        transferNote.Text = blocked ? standing.Blocked : status switch
        {
            1 => L.Transfers.RulerStays,
            2 => L.Transfers.CorpsLeaderMoves,
            5 => L.Officer.FreeNote(Transfers.LoyaltyOnJoining),
            6 => L.Officer.PrisonerNote(OfficerFields.Released),
            7 or 8 => L.Officer.HiddenNote,
            9 => L.Officer.DeadNote,
            _ => L.Officer.ServingNote(Transfers.LoyaltyOnJoining, OfficerFields.Dismissed)
        };
    }

    // The game's names, once per connection.
    internal void SetCatalog(GameCatalog names)
    {
        this.names = names;
        foreach (NameChoice choice in personalities) choice.SetNames(names.Personalities);
        ideology.SetNames(names.Ideologies);
        policy.SetNames(names.Policies);
        uniqueTactic.SetNames(names.Tactics);
        tacticNames = names.Tactics;
        gender.SetNames([new GameName(0, L.Officer.Male, ""), new GameName(1, L.Officer.Female, "")]);
        policyLevel.SetNames(Enumerable.Range(1, 5).Select(level => new GameName(level, level.ToString(CultureInfo.CurrentCulture), "")));
        // 없음 first, then the offices from the highest, each with its grade (the description begins "6품 · ...").
        office.SetNames(names.Offices.OrderBy(o => o.Id != OfficerFields.NoOffice).ThenBy(o => o.Id)
            .Select(o => o.Id == OfficerFields.NoOffice ? o : o with { Name = $"{o.Name} · {o.Description.Split(" · ")[0]}" }));
        for (int id = 1; id <= OfficerFields.CommonTactics; id++)
        {
            string name = GameCatalog.NameOf(names.Tactics, id);
            ((WC.TextBlock)tacticChips[id].Child).Text = name.Length > 0 ? name : id.ToString(CultureInfo.CurrentCulture);
        }
    }

    // Refresh office labels after a rule change while retaining the current office and pending choice.
    internal void FollowOffices(GameName[] offices)
    {
        if (names.Offices.SequenceEqual(offices)) return;
        int current = office.Current, picked = office.Picked;
        bool wasFilling = filling; filling = true;
        try
        {
            names = names with { Offices = offices };
            office.SetNames(offices.OrderBy(o => o.Id != OfficerFields.NoOffice).ThenBy(o => o.Id)
                .Select(o => o.Id == OfficerFields.NoOffice ? o : o with { Name = $"{o.Name} · {o.Description.Split(" · ")[0]}" }));
            office.Show(current); office.Pick(picked);
        }
        finally { filling = wasFilling; }
        if (OfficerId != 0) { NoteOffice(); Mark(); }
    }

    // Fills the card for an officer: others are every other officer, sworn the members of his 의형제 group.
    // items are the 명품 that can change hands (found or not yet found); holders, the offices other officers of his
    // force hold (by office, their names).
    internal void Open(OfficerRecord officer, Standing standing, IReadOnlyList<Other> others, IReadOnlyList<int> swornGroup,
        IReadOnlyList<Treasure> items, IReadOnlyDictionary<int, string> holders, IReadOnlyList<Transfers.ForceBases> forces)
    {
        filling = true;
        if (officer.Id != OfficerId) StartOver();
        OfficerId = officer.Id;
        record = officer.Data;
        Title.Text = officer.CourtesyName.Length > 0 ? $"{officer.Name} ({officer.CourtesyName})" : officer.Name;
        Shield.Colour = standing.Colour;
        ShowStanding(standing);
        labels = others.ToDictionary(o => o.Id, o => o.Label);

        loyalty.Show(officer[OfficerFields.Loyalty]);
        merit.Show(officer[OfficerFields.Merit]);
        for (int i = 0; i < abilities.Length; i++) abilities[i].Show(officer[OfficerFields.Abilities[i]]);
        for (int i = 0; i < aiTraits.Length; i++) aiTraits[i].Show(officer[AiTraits.Fields[i]]);
        birth.Show(officer[OfficerFields.Birth]);
        debut.Show(officer[OfficerFields.Debut]);
        death.Show(officer[OfficerFields.Death]);
        compatibility.Show(officer[OfficerFields.Compatibility]);
        chart.Colour = CardSkin.Color(standing.Colour);

        gender.Show((int)officer[OfficerFields.Gender]);
        ideology.Show((int)officer[OfficerFields.Ideology]);
        policy.Show((int)officer[OfficerFields.Policy]);
        uniqueTactic.Show((int)officer[OfficerFields.UniqueTactic]);
        policyLevel.Show((int)officer[OfficerFields.PolicyLevel]);
        office.Show((int)officer[OfficerFields.Office]);
        officeHolders = holders;
        // Only an officer serving a ruler holds an office (no ruler, free officer or prisoner held one).
        office.Box.IsEnabled = OfficerFields.IsVassal(officer.Status);
        Note(office.Box.IsEnabled ? "" : L.Officer.OfficeOnlyVassals);

        int used = OfficerFields.Personalities.Select((field, slot) => officer[field] != 0 ? slot + 1 : 0).Max();
        shownPersonalities = Math.Max(UsualPersonalities, used);
        for (int slot = 0; slot < personalities.Length; slot++)
        {
            personalities[slot].Show((int)officer[OfficerFields.Personalities[slot]]);
            personalities[slot].Box.Visibility = slot < shownPersonalities ? W.Visibility.Visible : W.Visibility.Collapsed;
        }

        currentTactics.Clear();
        foreach (FieldDef field in OfficerFields.Tactics)
            if (officer[field] is > 0 and <= OfficerFields.CommonTactics and var id) currentTactics.Add((int)id);
        keptTactics = OfficerFields.Tactics.Select(f => (int)officer[f]).Where(id => id > OfficerFields.CommonTactics).ToList();
        forgottenTactics.Clear();
        keptPanel.Children.Clear();
        keptChips.Clear();
        foreach (int id in keptTactics)
        {
            WC.Border chip = TacticChip(() => ToggleKept(id));
            ((WC.TextBlock)chip.Child).Text = GameCatalog.NameOf(tacticNames, id) is { Length: > 0 } name ? name : L.Officer.TacticNumber(id);
            keptChips.Add(chip);
            keptPanel.Children.Add(chip);
        }
        keptPanel.Visibility = keptTactics.Count == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        pickedTactics.Clear();
        pickedTactics.UnionWith(currentTactics);
        tacticNote.Text = "";
        ShowFormations(officer);

        // A spouse is of the other sex; the one the officer has stays on the list whatever it is.
        currentSpouse = (int)officer[OfficerFields.Spouse];
        int sex = (int)officer[OfficerFields.Gender];
        List<Other> partners = others.Where(o => o.Gender != sex || o.Id == currentSpouse).ToList();
        spouseValues = [0, .. partners.Select(o => o.Id)];
        spouse.ItemsSource = new List<string> { L.Terms.None }.Concat(partners.Select(o => o.Label)).ToList();
        spouse.SelectedIndex = Math.Max(0, spouseValues.IndexOf(currentSpouse));

        // 의형제 shows a fellow member: the group's own officer when that is someone else, else the first other member.
        otherValues = [0, .. others.Select(o => o.Id)];
        List<string> otherLabels = [L.Terms.None, .. others.Select(o => o.Label)];
        sworn.ItemsSource = otherLabels;
        currentSworn = swornGroup.Count == 0 ? 0 : swornGroup.Contains((int)officer[OfficerFields.Sworn]) && officer[OfficerFields.Sworn] != officer.Id
            ? (int)officer[OfficerFields.Sworn] : swornGroup.FirstOrDefault(id => id != officer.Id);
        sworn.SelectedIndex = Math.Max(0, otherValues.IndexOf(currentSworn));
        swornMembers.Inlines.Clear();
        if (swornGroup.Count > 0)
        {
            Fill(swornMembers, swornGroup.Select(id => id == officer.Id ? (WD.Inline)new WD.Run(officer.Name) : Link(OtherName(id), new OfficerTarget(id))));
            swornMembers.Inlines.InsertBefore(swornMembers.Inlines.FirstInline, new WD.Run(L.Officer.SwornGroup + " "));
        }
        swornGroupLine.Visibility = swornGroup.Count == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;

        likes.Open(officer, otherValues, otherLabels, OtherName);
        dislikes.Open(officer, otherValues, otherLabels, OtherName);
        treasures = [.. items];
        offered = treasures.Where(t => t.Owner != officer.Id).ToList();
        give.ItemsSource = new List<string> { L.Terms.None }.Concat(offered.Select(t => $"{t.Name} · {(t.Holder.Length > 0 ? t.Holder : L.Item.NoOwner)}")).ToList();
        give.SelectedIndex = -1;
        give.Text = "";
        given.Clear();
        RenderTreasures();
        ShowTransfers(officer, standing, forces);
        filling = false;
        Mark();
    }

    // The game's values now: boxes nobody typed in follow them, and the standing line is redrawn.
    internal void Follow(OfficerRecord officer, Standing standing)
    {
        if (officer.Id != OfficerId) return;
        loyalty.Follow(officer[OfficerFields.Loyalty]);
        merit.Follow(officer[OfficerFields.Merit]);
        for (int i = 0; i < abilities.Length; i++) abilities[i].Follow(officer[OfficerFields.Abilities[i]]);
        for (int i = 0; i < aiTraits.Length; i++) aiTraits[i].Follow(officer[AiTraits.Fields[i]]);
        birth.Follow(officer[OfficerFields.Birth]);
        debut.Follow(officer[OfficerFields.Debut]);
        death.Follow(officer[OfficerFields.Death]);
        compatibility.Follow(officer[OfficerFields.Compatibility]);
        ShowStanding(standing);
        // A new status brings other transfers; otherwise only what he is doing now (the pickers keep their choice).
        if (officer.Status != status) ShowTransfers(officer, standing, forcePosts);
        else ShowNow(standing);
        Mark();
    }

    // Takes ownership of nothing: the image is frozen.
    internal void SetPortrait(WMI.BitmapSource? image) => portrait.Source = image;

    private void ShowStanding(Standing standing)
    {
        var parts = new List<WD.Inline>
        {
            standing.Force > 0 ? Link(standing.ForceName, new ForceTarget(standing.Force)) : new WD.Run(standing.ForceName),
            new WD.Run(standing.Status)
        };
        if (standing.BaseName.Length > 0)
            parts.Add(BaseLink(standing) ?? new WD.Run(standing.BaseName));
        if (standing.Office.Length > 0) parts.Add(new WD.Run(L.Officer.OfficeOf(standing.Office)));
        Fill(Subtitle, parts);
    }

    // His base's name opening its card: a city's or a gate's (a tribe's base has none); null for another base.
    private WD.Inline? BaseLink(Standing standing) => standing.Base switch
    {
        >= 1 and <= GameMemory.CityCount => Link(standing.BaseName, new CityTarget(standing.Base)),
        >= GameMemory.GateFirst and <= GameMemory.GateLast => Link(standing.BaseName, new GateTarget(standing.Base)),
        _ => null
    };

    private void OpenOfficer(int id) => RaiseLink(new OfficerTarget(id));

    private void Give()
    {
        if (filling || give.SelectedIndex <= 0) return;
        Treasure treasure = offered[give.SelectedIndex - 1];
        give.Dispatcher.BeginInvoke(() =>
        {
            filling = true;
            give.SelectedIndex = -1;
            give.Text = "";
            filling = false;
        });
        if (given.Contains(treasure.Index)) return;
        given.Add(treasure.Index);
        RenderTreasures();
        Mark();
    }

    // The 명품 he holds, then those to be given (ringed, with a ✕ to take back the pick).
    private void RenderTreasures()
    {
        treasureChips.Children.Clear();
        foreach (Treasure treasure in treasures.Where(t => t.Owner == OfficerId))
            treasureChips.Children.Add(Chip(treasure.Name, treasure.Tip, added: false, null));
        foreach (int index in given)
        {
            Treasure treasure = treasures.First(t => t.Index == index);
            string from = treasure.Holder.Length > 0 ? L.Officer.TakenFrom(treasure.Holder) : L.Officer.GivenFromNone;
            treasureChips.Children.Add(Chip(treasure.Name, treasure.Tip.Length > 0 ? $"{from} · {treasure.Tip}" : from, added: true, () =>
            {
                given.Remove(index);
                RenderTreasures();
                Mark();
            }));
        }
        if (treasureChips.Children.Count == 0) treasureChips.Children.Add(new WC.TextBlock { Text = L.Terms.None, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(2, 1, 0, 1) });
    }

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal OfficerEdits.Request? Changes(out string error)
    {
        error = "";
        var own = new List<FieldChange>();
        void Add(FieldDef field, long value)
        {
            if (field.Read(record) != value) own.Add(new FieldChange(field, value));
        }
        foreach (NumberField field in Numbers())
        {
            if (!field.IsChanged) continue;
            if (!field.TryRead(out long value, out error)) return null;
            own.Add(new FieldChange(field.Field, value));
        }
        if (spouse.SelectedIndex < 0 || sworn.SelectedIndex < 0)
        {
            error = L.Officer.PickListedOfficer;
            (spouse.SelectedIndex < 0 ? spouse : sworn).Focus();
            return null;
        }
        Add(OfficerFields.Gender, gender.Picked);
        Add(OfficerFields.Ideology, ideology.Picked);
        Add(OfficerFields.Policy, policy.Picked);
        Add(OfficerFields.UniqueTactic, uniqueTactic.Picked);
        Add(OfficerFields.PolicyLevel, policyLevel.Picked);
        if (office.Box.IsEnabled) Add(OfficerFields.Office, office.Picked);
        // 개성 are written from the first slot on, without gaps, each once.
        List<int> traits = personalities.Take(shownPersonalities).Select(p => p.Picked).Where(id => id != 0).Distinct().ToList();
        for (int slot = 0; slot < shownPersonalities; slot++) Add(OfficerFields.Personalities[slot], slot < traits.Count ? traits[slot] : 0);
        if (TacticsChanged)
        {
            long[] slots = OfficerEdits.TacticSlots(keptTactics.Where(id => !forgottenTactics.Contains(id)).Select(id => (long)id), pickedTactics);
            for (int slot = 0; slot < slots.Length; slot++) Add(OfficerFields.Tactics[slot], slots[slot]);
        }
        Add(OfficerFields.Formations, pickedFormations);
        likes.AddChanges(Add);
        dislikes.AddChanges(Add);
        int spouseValue = spouseValues[spouse.SelectedIndex], swornValue = otherValues[sworn.SelectedIndex];
        return new OfficerEdits.Request(own, spouseValue != currentSpouse ? spouseValue : null, swornValue != currentSworn ? swornValue : null, [.. given]);
    }

    private IEnumerable<NumberField> Numbers() => abilities.Concat([loyalty, merit, birth, debut, death, compatibility]).Concat(aiTraits);

    private IEnumerable<NameChoice> Choices(int traits) =>
        personalities.Take(traits).Concat([gender, ideology, policy, policyLevel, office, uniqueTactic]);

    // An office another officer of the force holds goes from him to this one.
    private void NoteOffice()
    {
        if (filling || !office.Box.IsEnabled) return;
        Note(office.Picked != OfficerFields.NoOffice && officeHolders.TryGetValue(office.Picked, out string? holder)
            ? L.Officer.OfficeTaken(holder) : "");
    }

    private void Note(string text)
    {
        officeNote.Text = text;
        officeNote.Visibility = text.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    private string OtherName(int id) => labels.TryGetValue(id, out string? label) ? label : L.Terms.OfficerNumber(id);

    private bool TacticsChanged => forgottenTactics.Count > 0 || !pickedTactics.SetEquals(currentTactics);

    private int TacticsLearned => keptTactics.Count(id => !forgottenTactics.Contains(id)) + pickedTactics.Count;

    private void ToggleTactic(int id)
    {
        if (!pickedTactics.Remove(id))
        {
            if (!RoomForTactic()) return;
            pickedTactics.Add(id);
        }
        tacticNote.Text = "";
        Mark();
    }

    // A tactic beyond the common ones can be forgotten, and taken back while the card is open.
    private void ToggleKept(int id)
    {
        if (!forgottenTactics.Add(id))
        {
            if (!RoomForTactic()) return;
            forgottenTactics.Remove(id);
        }
        tacticNote.Text = "";
        Mark();
    }

    // A chip for each 진형 he may be taught, named as the game's table names it.
    private void ShowFormations(OfficerRecord officer)
    {
        currentFormations = pickedFormations = officer[OfficerFields.Formations];
        formationPanel.Children.Clear();
        formationChips.Clear();
        foreach (int id in OfficerFields.FormationsFor(officer.Id))
        {
            int formation = id;
            WC.Border chip = TacticChip(() =>
            {
                pickedFormations ^= 1L << formation;
                Mark();
            });
            ((WC.TextBlock)chip.Child).Text = names.Formation(formation).Name;
            formationChips[formation] = chip;
            formationPanel.Children.Add(chip);
        }
    }

    private bool RoomForTactic()
    {
        if (TacticsLearned < MaxTactics) return true;
        tacticNote.Text = L.Officer.TacticsFull(MaxTactics);
        return false;
    }

    private void Mark()
    {
        if (filling) return;
        int changed = Numbers().Count(field => field.IsChanged);
        foreach (NameChoice choice in Choices(shownPersonalities)) changed += choice.Mark() ? 1 : 0;
        for (int id = 1; id <= OfficerFields.CommonTactics; id++)
        {
            bool picked = pickedTactics.Contains(id), was = currentTactics.Contains(id);
            ShowChip(tacticChips[id], picked, was);
            tacticChips[id].ToolTip = picked != was ? (picked ? L.Officer.WillLearn : L.Officer.WillForget) : null;
        }
        for (int i = 0; i < keptChips.Count; i++)
        {
            bool forgotten = forgottenTactics.Contains(keptTactics[i]);
            ShowChip(keptChips[i], !forgotten, true);
            keptChips[i].ToolTip = forgotten ? L.Officer.WillForgetForGood : L.Officer.KeptTacticTip;
        }
        tacticCount.Text = L.Officer.TacticCount(TacticsLearned, MaxTactics);
        changed += TacticsChanged ? 1 : 0;
        foreach ((int id, WC.Border chip) in formationChips)
        {
            bool has = OfficerFields.HasFormation(pickedFormations, id), had = OfficerFields.HasFormation(currentFormations, id);
            ShowChip(chip, has, had);
            string mobility = names.Formation(id).Description;
            chip.ToolTip = has != had ? (has ? L.Officer.WillLearn : L.Officer.WillForget) : mobility.Length > 0 ? mobility : null;
        }
        changed += pickedFormations != currentFormations ? 1 : 0;
        bool spouseChanged = spouse.SelectedIndex < 0 || spouseValues[spouse.SelectedIndex] != currentSpouse;
        bool swornChanged = sworn.SelectedIndex < 0 || otherValues[sworn.SelectedIndex] != currentSworn;
        spouse.Background = spouseChanged ? CardSkin.Pending : CardSkin.Paper;
        sworn.Background = swornChanged ? CardSkin.Pending : CardSkin.Paper;
        changed += (spouseChanged ? 1 : 0) + (swornChanged ? 1 : 0) + likes.Mark() + dislikes.Mark() + given.Count;
        treasureFrame.Background = given.Count > 0 ? CardSkin.Pending : WM.Brushes.Transparent;
        chart.Show(abilities.Select(a => a.Current).ToArray(), abilities.Select(a => a.Value).ToArray());
        pending = changed;
        Count();
    }

    // The five abilities on a pentagon, the force's colour for the game's values and a dashed line for typed ones.
    private sealed class AbilityChart : W.FrameworkElement
    {
        private static readonly string[] Names = OfficerFields.Abilities.Select(f => f.Label).ToArray();
        private static readonly WM.Typeface Face = new(CardSkin.Font, W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);
        private long[] values = new long[5], typed = new long[5];

        internal WM.Color Colour { get; set; } = WM.Colors.Gray;

        internal void Show(long[] game, long[] edited)
        {
            values = game;
            typed = edited;
            InvalidateVisual();
        }

        protected override void OnRender(WM.DrawingContext dc)
        {
            double radius = Math.Min(ActualWidth, ActualHeight) / 2 - 24;
            var center = new W.Point(ActualWidth / 2, ActualHeight / 2 + 4);
            W.Point Vertex(int i, double value)
            {
                double angle = -Math.PI / 2 + i * 2 * Math.PI / 5, length = radius * Math.Clamp(value / 100.0, 0, 1.15);
                return new W.Point(center.X + length * Math.Cos(angle), center.Y + length * Math.Sin(angle));
            }
            WM.Geometry Polygon(Func<int, double> value)
            {
                var geometry = new WM.StreamGeometry();
                using (WM.StreamGeometryContext context = geometry.Open())
                {
                    context.BeginFigure(Vertex(0, value(0)), true, true);
                    for (int i = 1; i < 5; i++) context.LineTo(Vertex(i, value(i)), true, false);
                }
                geometry.Freeze();
                return geometry;
            }
            var grid = new WM.Pen(new WM.SolidColorBrush(WM.Color.FromArgb(70, CardSkin.Color(Theme.Wood).R, CardSkin.Color(Theme.Wood).G, CardSkin.Color(Theme.Wood).B)), 1);
            for (int level = 20; level <= 100; level += 20) dc.DrawGeometry(null, grid, Polygon(_ => level));
            for (int i = 0; i < 5; i++) dc.DrawLine(grid, center, Vertex(i, 100));
            var fill = new WM.SolidColorBrush(WM.Color.FromArgb(110, Colour.R, Colour.G, Colour.B));
            dc.DrawGeometry(fill, new WM.Pen(new WM.SolidColorBrush(Colour), 2), Polygon(i => values[i]));
            bool edited = !typed.SequenceEqual(values);
            if (edited) dc.DrawGeometry(null, new WM.Pen(CardSkin.Highlight, 2) { DashStyle = WM.DashStyles.Dash }, Polygon(i => typed[i]));
            double pixels = WM.VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < 5; i++)
            {
                W.Point p = Vertex(i, 100);
                double angle = -Math.PI / 2 + i * 2 * Math.PI / 5;
                bool changed = typed[i] != values[i];
                var text = new WM.FormattedText($"{Names[i]} {(changed ? typed[i] : values[i])}", CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, Face, 12,
                    changed ? CardSkin.Highlight : CardSkin.Ink, pixels);
                dc.DrawText(text, new W.Point(p.X + Math.Cos(angle) * 14 - text.Width / 2, p.Y + Math.Sin(angle) * 11 - text.Height / 2));
            }
        }
    }

    // 친애 or 혐오: the officers as chips (their names open their cards) with a ✕, and a box to add one (up to
    // eight). Written in ascending order.
    private sealed class TieList
    {
        internal readonly WC.StackPanel Panel = new() { Margin = new W.Thickness(0, 4, 0, 0) };
        private readonly WC.WrapPanel chips = new();
        private readonly WC.Border frame = new() { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(1), Padding = new W.Thickness(3), MinHeight = 28 };
        private readonly WC.ComboBox add = OfficerChoice();
        private readonly FieldDef[] fields;
        private readonly string name;
        private readonly Action<int> open;
        private List<int> current = [], picked = [];
        private List<int> values = [];
        private Func<int, string> label = id => id.ToString(CultureInfo.CurrentCulture);
        private int self;
        private bool filling;

        internal event Action? Changed;

        internal TieList(string name, FieldDef[] fields, Action<int> open)
        {
            this.name = name;
            this.fields = fields;
            this.open = open;
            frame.Child = chips;
            add.ToolTip = L.Officer.AddTieTip(name, MaxTies);
            Panel.Children.Add(Line(name, add));
            Panel.Children.Add(frame);
            // Typing a name moves the pick from one match to the next ("조" finds 조광 before "조운" is typed), so
            // an officer is added only when the pick is taken: Enter, or a click in the list.
            add.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != WI.Key.Enter) return;
                e.Handled = true;
                Take();
            };
            add.DropDownClosed += (_, _) => Take();
        }

        private void Take()
        {
            if (filling || add.SelectedIndex <= 0) return;
            int id = values[add.SelectedIndex];
            // The box empties again once the pick has been taken (not while it is still closing).
            add.Dispatcher.BeginInvoke(() =>
            {
                filling = true;
                add.SelectedIndex = -1;
                add.Text = "";
                filling = false;
            });
            if (id == self || picked.Contains(id) || picked.Count >= MaxTies) return;
            picked.Add(id);
            Render();
            Changed?.Invoke();
        }

        internal void Open(OfficerRecord officer, List<int> others, List<string> otherLabels, Func<int, string> names)
        {
            filling = true;
            self = officer.Id;
            label = names;
            values = others;
            add.ItemsSource = otherLabels;
            add.SelectedIndex = -1;
            add.Text = "";
            current = fields.Select(f => (int)officer[f]).Where(id => id != 0).ToList();
            picked = [.. current];
            filling = false;
            Render();
        }

        private void Render()
        {
            chips.Children.Clear();
            foreach (int id in picked)
            {
                int tie = id;
                chips.Children.Add(Chip(label(id), current.Contains(id) ? "" : L.Officer.NewTie(name), added: !current.Contains(id), () =>
                {
                    picked.Remove(tie);
                    Render();
                    Changed?.Invoke();
                }, () => open(tie)));
            }
            if (picked.Count == 0) chips.Children.Add(new WC.TextBlock { Text = L.Terms.None, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(2, 1, 0, 1) });
        }

        private bool IsChanged => !picked.Order().SequenceEqual(current.Order());

        // Yellow when changed; 1 when changed, else 0 (for the badge).
        internal int Mark()
        {
            frame.Background = IsChanged ? CardSkin.Pending : WM.Brushes.Transparent;
            return IsChanged ? 1 : 0;
        }

        internal void AddChanges(Action<FieldDef, long> add)
        {
            if (!IsChanged) return;
            List<int> ordered = picked.Order().ToList();
            for (int slot = 0; slot < fields.Length; slot++) add(fields[slot], slot < ordered.Count ? ordered[slot] : 0);
        }
    }
}
