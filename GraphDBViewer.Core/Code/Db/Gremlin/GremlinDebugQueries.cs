namespace GraphDBViewerWeb.Code;

///<summary>
///Builds the debugger's live-server queries — currently the gdotV-style "traversal simulation" that
///projects, for each element at a step, whether it reaches the query's final result and how many results
///it yields. Pure string work over the parsed steps, so it's unit-tested without a database.
///</summary>
public static class GremlinDebugQueries
{
    //Steps whose output isn't graph elements, so id()/label() can't run on them — a simulation is disabled
    //here (mirrors gdotV disabling by/project/group/groupCount and the like).
    private static readonly HashSet<string> NonElementSteps = new()
    {
        "values", "valueMap", "elementMap", "propertyMap", "properties",
        "id", "label", "key", "value",
        "count", "sum", "max", "min", "mean",
        "group", "groupCount", "project", "path", "tree", "math", "constant", "fold", "cap", "select", "by"
    };

    ///<summary>
    ///The traversal after the step (starting with "."), with trailing terminal steps (toList/next/...)
    ///removed, or null when there's nothing downstream to simulate toward.
    ///</summary>
    public static string SimulationSuffix(string fullQuery, GremlinStep step)
    {
        if (step == null || string.IsNullOrEmpty(fullQuery))
            return null;

        var steps = GremlinStepParser.Parse(fullQuery);

        GremlinStep last = null;
        foreach (var s in steps)
            if (s.Start >= step.End && !s.IsTerminal)
                last = s;

        if (last == null)
            return null;

        return fullQuery.Substring(step.End, last.End - step.End).TrimStart();
    }

    ///<summary>True when a traversal simulation can run at this step: it yields graph elements and has
    ///downstream steps to project toward.</summary>
    public static bool CanSimulate(string fullQuery, GremlinStep step)
    {
        if (step == null || NonElementSteps.Contains(step.Name))
            return false;

        return SimulationSuffix(fullQuery, step) != null;
    }

    ///<summary>
    ///Builds a projection that, for each element at the step (up to elementLimit), reports its id/label/name,
    ///whether it reaches the query's final result (survives), and how many final results it yields (outCount).
    ///Returns null when the step can't be simulated.
    ///</summary>
    public static string BuildSimulation(string fullQuery, GremlinStep step, int elementLimit)
    {
        var suffix = SimulationSuffix(fullQuery, step);
        if (suffix == null || NonElementSteps.Contains(step.Name))
            return null;

        var prefix = fullQuery.Substring(0, step.End);
        var anon = "__" + suffix;

        return $"{prefix}.limit({elementLimit}).project('id','label','name','survives','outCount').by(id()).by(label()).by(coalesce(values('name'),constant(''))).by({anon}.limit(1).count()).by({anon}.count())";
    }

    ///<summary>The debuggable, non-terminal steps after `step` — the "remaining steps" a single element is
    ///traced through in a traverser inspection.</summary>
    public static List<GremlinStep> RemainingSteps(string fullQuery, GremlinStep step)
    {
        if (step == null || string.IsNullOrEmpty(fullQuery))
            return new List<GremlinStep>();

        return GremlinStepParser.Parse(fullQuery)
            .Where(s => s.Start >= step.End && s.Debuggable && !s.IsTerminal)
            .ToList();
    }

    ///<summary>Formats an element id for hasId(): numeric ids bare (e.g. 964), everything else single-quoted.</summary>
    public static string FormatDebugId(string id)
    {
        if (long.TryParse(id, out _))
            return id;

        return "'" + (id ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    ///<summary>
    ///A count query that re-enters the traversal at `step` filtered to the single element `id`, then applies
    ///the steps up to and including `uptoStep` — for tracing one element through the rest of the query and
    ///finding where it drops out. Re-running the real prefix (rather than g.V(id)) preserves earlier context.
    ///</summary>
    public static string BuildInspectionCount(string fullQuery, GremlinStep step, string id, GremlinStep uptoStep)
    {
        if (step == null || uptoStep == null)
            return null;

        var prefix = fullQuery.Substring(0, step.End);
        var span = fullQuery.Substring(step.End, uptoStep.End - step.End).TrimStart();

        return $"{prefix}.hasId({FormatDebugId(id)}).limit(1){span}.count()";
    }
}
