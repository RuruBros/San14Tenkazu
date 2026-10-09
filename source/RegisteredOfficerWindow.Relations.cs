using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed partial class RegisteredOfficerWindow
{
    private readonly Dictionary<string, RegisteredRelationList> relationLists = [];

    private WC.StackPanel Relations()
    {
        var panel = new WC.StackPanel();
        var parents = new WC.WrapPanel();
        foreach (FieldDef field in RegisteredOfficerRelations.Direct) parents.Children.Add(Cell(field.Label, Choice(field, [])));
        panel.Children.Add(parents);
        panel.Children.Add(new WC.TextBlock { Text = L.Registered.GroupNote, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap });
        foreach (var (key, label) in new[] { ("clan", L.Registered.ClanMembers), ("sworn", L.Registered.SwornMembers), ("likes", L.Officer.Likes), ("dislikes", L.Officer.Dislikes) })
        {
            panel.Children.Add(Heading(label));
            var control = new RegisteredRelationList();
            relationLists.Add(key, control);
            panel.Children.Add(control.Panel);
        }
        var advanced = new WC.WrapPanel();
        advanced.Children.Add(Cell(RegisteredOfficerFields.Clan.Label, Number(RegisteredOfficerFields.Clan)));
        advanced.Children.Add(Cell(RegisteredOfficerFields.Sworn.Label, Number(RegisteredOfficerFields.Sworn)));
        panel.Children.Add(new WC.Expander { Header = L.Registered.GroupNumbers, Content = advanced, Margin = new W.Thickness(0, 12, 0, 0) });
        return panel;
    }

    private GameName[] RelationNames() => RegisteredOfficerNames.Get("officer").Concat(working.Values.Select(o => new GameName(o.Id, o.Name, "")))
        .GroupBy(o => o.Id).Select(g => g.Last()).OrderBy(o => o.Name, StringComparer.CurrentCulture).ToArray();

    private void ShowRelations()
    {
        if (selected is null || file is null) return;
        int self = selected.Id;
        GameName[] names = RelationNames().Where(o => o.Id != self).ToArray();
        foreach (var (key, field) in new[] { ("clan", RegisteredOfficerFields.Clan), ("sworn", RegisteredOfficerFields.Sworn) })
        {
            int[] members = RegisteredOfficerRelations.Members(working.Values, self, field);
            int[] baseline = RegisteredOfficerRelations.Members(file.Officers, self, field);
            relationLists[key].Show(names.Where(n => working.ContainsKey(n.Id)).ToArray(), members, baseline,
                id => ChangeGroup(field, id, true), id => ChangeGroup(field, id, false));
        }
        foreach (var (key, fields) in new[] { ("likes", RegisteredOfficerFields.Likes), ("dislikes", RegisteredOfficerFields.Dislikes) })
        {
            int[] members = fields.Select(f => (int)f.Read(selected.Data)).Where(id => id != 0).ToArray();
            byte[] original = file.Officers.Single(o => o.Id == self).Data;
            int[] baseline = fields.Select(f => (int)f.Read(original)).Where(id => id != 0).ToArray();
            relationLists[key].Show(names, members, baseline, id => ChangeTies(fields, id, true), id => ChangeTies(fields, id, false));
            relationLists[key].Add.Box.IsEnabled = members.Length < fields.Length;
        }
    }

    private void ChangeGroup(FieldDef field, int member, bool add)
    {
        RefreshGameState();
        if (gameRunning || selected is null || file is null || !CaptureCurrent()) return;
        try
        {
            RegisteredOfficer[] changes = add ? RegisteredOfficerRelations.AddMember(working.Values, selected.Id, member, field)
                : RegisteredOfficerRelations.RemoveMember(working.Values, selected.Id, member, field);
            _ = file.Compose(changes);
            foreach (RegisteredOfficer officer in changes) working[officer.Id] = officer;
            ShowOfficer(working[selected.Id]);
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { Error(ex); }
    }

    private void ChangeTies(FieldDef[] fields, int member, bool add)
    {
        RefreshGameState();
        if (gameRunning || selected is null || file is null || !CaptureCurrent()) return;
        try
        {
            List<int> ids = fields.Select(f => (int)f.Read(selected.Data)).Where(id => id != 0).ToList();
            if (add) ids.Add(member); else ids.Remove(member);
            RegisteredOfficer updated = RegisteredOfficerRelations.SetTies(selected, fields, ids);
            _ = file.Compose([updated]);
            working[updated.Id] = updated;
            ShowOfficer(updated);
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex)) { Error(ex); }
    }
}
