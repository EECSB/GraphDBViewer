using System;
using System.IO;
using Xunit;
using Xunit.Sdk;

namespace GraphDBViewerWeb.Tests;

///<summary>
///A real document to run the knowledge-graph code over, rather than three sentences written to agree
///with whatever the code does. Dropped into Assets/ and copied beside the test binary.
///
///Optional on purpose. The file is a large lump of prose that nobody wants in a diff and that a fresh
///clone has no way to obtain, so the tests that need it say so and skip when it is missing instead of
///failing. Everything else in the suite runs regardless.
///</summary>
public static class SampleDocument
{
    public const string FileName = "nuclear-knowledge-graph.txt";

    ///<summary>Where the asset lands beside the test binary.</summary>
    public static string Path
    {
        get { return System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", FileName); }
    }

    public static bool Exists
    {
        get { return File.Exists(Path); }
    }

    ///<summary>The document's text. Only call it where <see cref="Exists"/> has been checked.</summary>
    public static string Text
    {
        get { return File.ReadAllText(Path); }
    }
}

///<summary>
///A fact that needs <see cref="SampleDocument"/>, and is skipped when it is not there.
///</summary>
public sealed class SampleDocumentFactAttribute : FactAttribute
{
    public SampleDocumentFactAttribute()
    {
        if (!SampleDocument.Exists)
            Skip = $"Needs Assets/{SampleDocument.FileName}, which this checkout does not carry.";
    }
}

///<summary>
///A fact that spends real money: it calls a live model with a real key. Skipped unless someone asks for
///it by name, because a suite that quietly bills an API every time it runs is a suite people stop running.
///
///Set GDBV_LIVE_AI=1 to include these. They also need the sample document and a filled-in dev-secrets.json.
///</summary>
public sealed class LiveAiFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "GDBV_LIVE_AI";

    public LiveAiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            Skip = $"Spends real API credits. Set {EnvironmentVariable}=1 to run.";
        else if (!SampleDocument.Exists)
            Skip = $"Needs Assets/{SampleDocument.FileName}, which this checkout does not carry.";
    }
}
