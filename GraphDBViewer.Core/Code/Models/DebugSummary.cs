using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///What the query debugger's Steps tab says about each step beside its count (gdotV's Traverser Summary, DESIGN.md's
///debugger polish): the count drawn after the step in the query itself, and, for the step in focus, what went into it
///and what came out, and what it did between the two. Pure, so the sentences are tested where they are made.
///</summary>
public static class DebugSummary
{
    ///<summary>
    ///The steps' ranges for the editor, each with the label drawn after it: its count once known, or "error". The steps
    ///were read from the query trimmed and the editor holds it as typed, so each range moves on by its leading spaces.
    ///</summary>
    public static string RangesJson(IEnumerable<DebugStepResult> steps, int lead)
    {
        var ranges = (steps ?? []).Select(d => new object[] { d.Step.Start + lead, d.Step.End + lead, Label(d) }).ToList();

        return JsonSerializer.Serialize(ranges);
    }

    ///<summary>How far a query as typed runs ahead of the query trimmed: its leading whitespace.</summary>
    public static int Lead(string typed)
    {
        var text = typed ?? "";

        return text.Length - text.TrimStart().Length;
    }

    ///<summary>The label drawn after a step in the editor: its count, "error", or none while it runs.</summary>
    public static string Label(DebugStepResult step)
    {
        if (step.Error != null)
            return "error";
        else if (step.Count is null)
            return null;

        return step.Count.Value.ToString("N0", CultureInfo.InvariantCulture);
    }

    ///<summary>A step's traversers in words, for the summary's two halves.</summary>
    public static string Traversers(DebugStepResult step)
    {
        if (step == null)
            return "the traversal's start";
        else if (step.Error != null)
            return "an error";
        else if (step.Count is null)
            return "…";
        else if (step.Count == 1)
            return "1 traverser";

        return step.Count.Value.ToString("N0", CultureInfo.InvariantCulture) + " traversers";
    }

    ///<summary>What a step did to the traversers it was given, in a sentence, or null when that cannot be said yet.</summary>
    public static string Change(long? into, long? outOf)
    {
        if (into is null || outOf is null)
            return null;

        var given = into.Value;
        var made = outOf.Value;

        if (made == 0 && given > 0)
            return $"None of the {N(given)} got through: this is where the traversal loses everything.";
        else if (made == given)
            return "As many came out as went in.";
        else if (made < given)
            return $"{N(made)} of the {N(given)} came through, and {N(given - made)} stopped here.";
        else if (given == 0)
            return null;

        return $"The {N(given)} became {N(made)}, {(made / (double)given).ToString("0.#", CultureInfo.InvariantCulture)} each on average.";
    }

    private static string N(long count)
    {
        return count.ToString("N0", CultureInfo.InvariantCulture);
    }
}
