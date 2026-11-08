using System;
using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///One turn of a conversation, in the provider-agnostic shape both request builders serialize from.
///
///Only the two roles a caller can author are here. An assistant turn that called a tool carries provider
///specific blocks — Anthropic's tool_use, OpenAI's tool_calls — which stay inside the loop that produced
///them: replaying them from here would mean rebuilding each provider's wire format from a lossy copy.
///What a caller replays instead is the text of that turn, which is what a later turn needs to read.
///</summary>
public sealed class LlmMessage
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    private static readonly IReadOnlyList<AiAttachment> None = Array.Empty<AiAttachment>();

    public LlmMessage(string role, string text, IReadOnlyList<AiAttachment> attachments = null)
    {
        Role = role;
        Text = text;
        Attachments = attachments ?? None;
    }

    public string Role { get; }

    public string Text { get; }

    ///<summary>The files a question was sent with, which go ahead of its words (<see cref="AiAttachment.Parts"/>). Empty for anything else.</summary>
    public IReadOnlyList<AiAttachment> Attachments { get; }

    public static LlmMessage FromUser(string text, IReadOnlyList<AiAttachment> attachments = null)
    {
        return new LlmMessage(UserRole, text, attachments);
    }

    public static LlmMessage FromAssistant(string text)
    {
        return new LlmMessage(AssistantRole, text);
    }

    ///<summary>The single-prompt case as a conversation of one, so one code path serves both.</summary>
    public static IReadOnlyList<LlmMessage> One(string userPrompt)
    {
        return new List<LlmMessage> { FromUser(userPrompt) };
    }
}
