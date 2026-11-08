using System;

namespace GraphDBViewerWeb.Code;

///<summary>
///A slash command of an AI window's own (<see cref="GraphDBViewerWeb.Components.AiChatWindow"/>): a question written out
///for the window, listed after the commands every window has, and sent at once when run, with any words typed after its
///name added to it ("/count products only"). Ask AI's are /describe and /count.
///</summary>
///<param name="Name">What is typed after the slash: lowercase letters only.</param>
///<param name="Says">What the list says it does.</param>
///<param name="Question">The question it sends.</param>
///<param name="WhyNot">Why it cannot run now, or null when it can: asked as the list is shown. A window already waits for a model and for an answer to come in.</param>
public sealed record AiSlashCommand(string Name, string Says, string Question, Func<string> WhyNot = null);
