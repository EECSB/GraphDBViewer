using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///A part of a page the panel board lays out (panel-boards spec): what Customize calls it and shows it with, and how it
///may be placed. Its content is the page's own, asked for by id when the board draws it.
///</summary>
public sealed class PanelInfo
{
    public string Id { get; init; }

    public string Title { get; init; }

    ///<summary>A Bootstrap Icons class, such as <c>bi-alarm</c>.</summary>
    public string Icon { get; init; }

    ///<summary>Needs a column at least half the page wide, and is never put in a narrower one.</summary>
    public bool Wide { get; init; }

    ///<summary>May not be hidden.</summary>
    public bool Required { get; init; }

    ///<summary>Whether it has anything to show now. A panel with nothing takes no room, and keeps its place.</summary>
    public bool Shown { get; init; } = true;
}

///<summary>A column of a preset's row: its width out of twelve, and its panels from the top.</summary>
public sealed class PanelColumn
{
    public PanelColumn(int width, params string[] panels)
    {
        Width = width;
        Panels = panels;
    }

    public int Width { get; }

    public IReadOnlyList<string> Panels { get; }
}

public sealed class PanelRow
{
    public PanelRow(params PanelColumn[] columns)
    {
        Columns = columns;
    }

    public IReadOnlyList<PanelColumn> Columns { get; }
}

///<summary>A ready-made arrangement a page offers: rows of columns, and the panels that start hidden. A page's first preset is its default.</summary>
public sealed class PanelPreset
{
    public string Id { get; init; }

    public string Name { get; init; }

    ///<summary>A Bootstrap Icons class, shown beside the name in the Layout menu.</summary>
    public string Icon { get; init; }

    public IReadOnlyList<PanelRow> Rows { get; init; } = [];

    public IReadOnlyList<string> Hidden { get; init; } = [];

    ///<summary>The columns in reading order, row after row: an arrangement counts its columns the same way.</summary>
    public IReadOnlyList<PanelColumn> Columns => Rows.SelectMany(r => r.Columns).ToList();
}

///<summary>What a person made of a preset: each column's panels from the top, the columns counted across the rows, and the hidden ones.</summary>
public sealed class PanelArrangement
{
    public List<List<string>> Columns { get; set; } = [];

    public List<string> Hidden { get; set; } = [];
}

///<summary>What is kept for a page in the person's workspace: the preset in use, and an arrangement for each preset they changed.</summary>
public sealed class PanelsKept
{
    public string Preset { get; set; }

    public Dictionary<string, PanelArrangement> Arrangements { get; set; } = [];

    ///<summary>Where a page's arrangement is kept, under the shell's storage: <c>graphdbviewer:board:treeality.scheduling/schedule</c>.</summary>
    public static string KeyFor(string page)
    {
        return "graphdbviewer:board:" + page;
    }
}

///<summary>A panel the board asks the page to draw: its id, and the width out of twelve of the column it is in.</summary>
public sealed record PanelSlot(string Id, int Width);

///<summary>
///The panel board's rules, apart from any page so they are tested apart: an arrangement from a preset, a kept one read
///against the page as it is now, and the moves Customize makes. Every move refuses what the page does not allow — a
///wide panel in a narrow column, a required panel hidden — and says whether anything changed.
///</summary>
public static class PanelArranging
{
    ///<summary>Narrower than this, out of twelve, a column takes no wide panel.</summary>
    public const int WideWidth = 6;

    ///<summary>The kept preset when the page still has it, and the page's default otherwise.</summary>
    public static PanelPreset PresetOf(PanelsKept kept, IReadOnlyList<PanelPreset> presets)
    {
        var chosen = presets.FirstOrDefault(p => p.Id == kept?.Preset);

        if (chosen != null)
            return chosen;

        return presets[0];
    }

    public static PanelArrangement FromPreset(PanelPreset preset)
    {
        return new PanelArrangement
        {
            Columns = preset.Columns.Select(c => c.Panels.ToList()).ToList(),
            Hidden = preset.Hidden.ToList()
        };
    }

    ///<summary>
    ///A kept arrangement read against the page as it is now (the design's five rules): one made for other columns is
    ///set aside for the preset; panels the page no longer has are dropped, and one listed twice keeps its first place;
    ///one the page gained goes where the preset puts it; a required one is shown; a wide one in a narrow column goes
    ///back where the preset puts it.
    ///</summary>
    public static PanelArrangement Resolve(PanelArrangement kept, PanelPreset preset, IReadOnlyList<PanelInfo> panels)
    {
        var columns = preset.Columns;
        PanelArrangement arranged;

        if (kept?.Columns == null || kept.Columns.Count != columns.Count)
            arranged = FromPreset(preset);
        else
            arranged = Copy(kept);

        var known = panels.ToDictionary(p => p.Id);
        var seen = new HashSet<string>();

        foreach (var column in arranged.Columns)
            column.RemoveAll(id => id == null || !known.ContainsKey(id) || !seen.Add(id));

        arranged.Hidden.RemoveAll(id => id == null || !known.ContainsKey(id) || !seen.Add(id));

        foreach (var id in arranged.Hidden.Where(id => known[id].Required).ToList())
        {
            arranged.Hidden.Remove(id);
            seen.Remove(id);
        }

        foreach (var panel in panels.Where(p => !seen.Contains(p.Id)))
        {
            if (preset.Hidden.Contains(panel.Id) && !panel.Required && !Named(preset, panel.Id))
                arranged.Hidden.Add(panel.Id);
            else
                Place(arranged, preset, panel);
        }

        for (var i = 0; i < arranged.Columns.Count; i++)
        {
            if (columns[i].Width >= WideWidth)
                continue;

            foreach (var id in arranged.Columns[i].Where(id => known[id].Wide).ToList())
            {
                arranged.Columns[i].Remove(id);
                Place(arranged, preset, known[id]);
            }
        }

        return arranged;
    }

    ///<summary>Where a panel is: its column and its place in it, or (-1, -1) when it is hidden or nowhere.</summary>
    public static (int Column, int Index) Position(PanelArrangement arranged, string id)
    {
        for (var c = 0; c < arranged.Columns.Count; c++)
        {
            var index = arranged.Columns[c].IndexOf(id);

            if (index >= 0)
                return (c, index);
        }

        return (-1, -1);
    }

    public static bool Fits(PanelInfo panel, PanelColumn column)
    {
        return !panel.Wide || column.Width >= WideWidth;
    }

    ///<summary>Puts a panel in a column before another panel there, or last when <paramref name="before"/> is not in it.</summary>
    public static bool Move(PanelArrangement arranged, PanelPreset preset, PanelInfo panel, int column, string before)
    {
        var columns = preset.Columns;

        if (column < 0 || column >= arranged.Columns.Count || !Fits(panel, columns[column]) || panel.Id == before)
            return false;

        var was = Position(arranged, panel.Id);
        var wasHidden = arranged.Hidden.Remove(panel.Id);

        if (was.Column >= 0)
            arranged.Columns[was.Column].RemoveAt(was.Index);

        var target = arranged.Columns[column];
        var at = target.IndexOf(before);

        if (at < 0)
            target.Add(panel.Id);
        else
            target.Insert(at, panel.Id);

        return wasHidden || Position(arranged, panel.Id) != was;
    }

    public static bool MoveUp(PanelArrangement arranged, PanelPreset preset, PanelInfo panel)
    {
        var (column, index) = Position(arranged, panel.Id);

        if (index <= 0)
            return false;

        return Move(arranged, preset, panel, column, arranged.Columns[column][index - 1]);
    }

    public static bool MoveDown(PanelArrangement arranged, PanelPreset preset, PanelInfo panel)
    {
        var (column, index) = Position(arranged, panel.Id);

        if (column < 0 || index >= arranged.Columns[column].Count - 1)
            return false;

        var after = arranged.Columns[column].ElementAtOrDefault(index + 2);

        return Move(arranged, preset, panel, column, after);
    }

    ///<summary>
    ///The column the panel would go to, one step before (-1) or after (+1) in reading order, skipping columns it does not
    ///fit; or -1 when there is none.
    ///</summary>
    public static int Beside(PanelArrangement arranged, PanelPreset preset, PanelInfo panel, int step)
    {
        var columns = preset.Columns;
        var (column, _) = Position(arranged, panel.Id);

        if (column < 0)
            return -1;

        for (var c = column + step; c >= 0 && c < columns.Count; c += step)
        {
            if (Fits(panel, columns[c]))
                return c;
        }

        return -1;
    }

    ///<summary>Moves a panel to the column before or after, at the same height there, or last.</summary>
    public static bool MoveBeside(PanelArrangement arranged, PanelPreset preset, PanelInfo panel, int step)
    {
        var target = Beside(arranged, preset, panel, step);

        if (target < 0)
            return false;

        var (_, index) = Position(arranged, panel.Id);

        return Move(arranged, preset, panel, target, arranged.Columns[target].ElementAtOrDefault(index));
    }

    public static bool Hide(PanelArrangement arranged, PanelInfo panel)
    {
        if (panel.Required || arranged.Hidden.Contains(panel.Id))
            return false;

        var (column, index) = Position(arranged, panel.Id);

        if (column >= 0)
            arranged.Columns[column].RemoveAt(index);

        arranged.Hidden.Add(panel.Id);

        return true;
    }

    ///<summary>A hidden panel shown again, where its preset puts it.</summary>
    public static bool Show(PanelArrangement arranged, PanelPreset preset, PanelInfo panel)
    {
        if (!arranged.Hidden.Remove(panel.Id))
            return false;

        Place(arranged, preset, panel);

        return true;
    }

    ///<summary>
    ///The widths a row's columns are drawn at when only some of them have anything to show: their preset widths, shared
    ///out over the whole twelve in proportion, so a column on its own takes the row.
    ///</summary>
    public static IReadOnlyList<int> Share(IReadOnlyList<int> widths)
    {
        var total = widths.Sum();

        if (widths.Count == 0 || total == 12)
            return widths;

        var shared = widths.Select(w => (int)Math.Round(w * 12.0 / total, MidpointRounding.AwayFromZero)).ToList();

        shared[^1] = 12 - shared.Take(shared.Count - 1).Sum();

        return shared;
    }

    ///<summary>
    ///A panel put where the preset puts it: after the panel that comes before it there, or first; or at the end of the
    ///first column it fits when the preset does not name it, or the preset's place is too narrow for it.
    ///</summary>
    private static void Place(PanelArrangement arranged, PanelPreset preset, PanelInfo panel)
    {
        var columns = preset.Columns;

        for (var c = 0; c < columns.Count; c++)
        {
            var named = columns[c].Panels.ToList();
            var index = named.IndexOf(panel.Id);

            if (index < 0 || !Fits(panel, columns[c]))
                continue;

            var target = arranged.Columns[c];
            var at = 0;

            for (var i = index - 1; i >= 0; i--)
            {
                var found = target.IndexOf(named[i]);

                if (found >= 0)
                {
                    at = found + 1;
                    break;
                }
            }

            target.Insert(at, panel.Id);

            return;
        }

        for (var c = 0; c < columns.Count; c++)
        {
            if (Fits(panel, columns[c]))
            {
                arranged.Columns[c].Add(panel.Id);

                return;
            }
        }

        arranged.Columns[Widest(columns)].Add(panel.Id);
    }

    private static bool Named(PanelPreset preset, string id)
    {
        return preset.Columns.Any(c => c.Panels.Contains(id));
    }

    private static int Widest(IReadOnlyList<PanelColumn> columns)
    {
        var widest = 0;

        for (var c = 1; c < columns.Count; c++)
        {
            if (columns[c].Width > columns[widest].Width)
                widest = c;
        }

        return widest;
    }

    private static PanelArrangement Copy(PanelArrangement kept)
    {
        return new PanelArrangement
        {
            Columns = kept.Columns.Select(c => (c ?? []).ToList()).ToList(),
            Hidden = (kept.Hidden ?? []).ToList()
        };
    }
}
