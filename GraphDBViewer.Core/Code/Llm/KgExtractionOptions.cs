namespace GraphDBViewerWeb.Code;

///<summary>
///What the model is asked for when it draws a graph from text: the knowledge-graph window's extraction options, back as the
///checkboxes they were there (2026-11-02, the user's "do it just like it was"), now in Ask AI's tool picker. All off, as they
///were: each costs tokens or a request, or narrows what the model may invent, and none should happen without being asked for.
///</summary>
///<param name="GroundInSchema">Labels from the connected graph's own, and no others; offered only where its schema was read.</param>
///<param name="SourceSentence">The sentence each entity and relationship came from, quoted, as a property of it.</param>
///<param name="Confidence">How sure the text is of each, 0 to 1; anything under a half is drawn in gray.</param>
///<param name="SplitLongText">A long text proposed in pieces of about <see cref="KgPrompt.PartChars"/> characters, merged by their ids.</param>
///<param name="AskAgain">After each graph, the model asked once more for what it missed, and that proposed too.</param>
public sealed record KgExtractionOptions(
    bool GroundInSchema = false,
    bool SourceSentence = false,
    bool Confidence = false,
    bool SplitLongText = false,
    bool AskAgain = false);
