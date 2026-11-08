namespace GraphDBViewerWeb.Code;

///<summary>What an AI chat window gives a card of its owner's (<c>AiChatWindow.TranscriptPanel</c>) each time it draws.</summary>
///<param name="ModelName">The model the next question goes to, or null with none picked.</param>
///<param name="Generating">Whether an answer is coming, which nothing can be sent over.</param>
public sealed record AiChatPanelState(string ModelName, bool Generating);
