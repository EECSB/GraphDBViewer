using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///A single clickable query example shown in the Examples tab: a friendly button label and the query it
///pastes into the editor. Engine-neutral — the same shape holds a Gremlin, Cypher or SPARQL example, so
///the tab can swap its whole list to match the editor's language.
///</summary>
///<param name="Name">Short label shown on the button.</param>
///<param name="Query">The query pasted into the editor when clicked.</param>
///<param name="Destructive">If true, the button is styled as a warning (drops / wipes data).</param>
public record QueryExample(string Name, string Query, bool Destructive = false);

///<summary>An ordered, named group of <see cref="QueryExample"/>s — renders as a labeled row of buttons.</summary>
public record QueryExampleGroup(string Category, IReadOnlyList<QueryExample> Examples);
