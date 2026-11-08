using System.Runtime.ExceptionServices;
using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GraphDBViewerWeb.Tests;

///<summary>
///Raises a DOM event in a bUnit test and returns once the event has run and its render is in the markup.
///
///bUnit's own <c>Click()</c>, <c>Change()</c> and the rest run the event straight away only when the renderer has nothing
///queued. When a render or a continuation from the step before is still waiting its turn, the event is queued behind it and
///the call returns at once: the test reads markup the event has not changed yet, and the element it found carries handler
///ids that the queued render replaces, so the event fails with <c>UnknownEventHandlerIdException</c> when its turn comes,
///where no catch around the call sees it. Wrapping the find and the event in <c>cut.InvokeAsync</c>, as bUnit's message
///suggests, makes it certain rather than curing it: an event raised from inside the renderer's dispatcher always goes to the
///back of its queue, so the <c>InvokeAsync</c> returns before the event has run. That, with bUnit before 2.12.5 caching
///the parse of an earlier render (bUnit #1938), was HomeMarkupTests' flake from 2026-11-03 to 2026-11-06: a few of its tests
///at random in a loaded full run, and some alone.
///
///Here the queue is waited out first, so the element is found in the markup as it stands; the event is raised from the
///test's own thread; and the queue is waited out again, which ends after the event and its render. An event that still
///came too late for the element it was given is raised again on the element found afresh. Only the event's synchronous
///part is waited for: a handler that goes on to await something (a held answer, a delay) is left to it, as the page would.
///</summary>
internal static class RenderedEvents
{
    private const int Attempts = 10;

    //Raises what raise raises on the element find returns, as above.
    public static void Raise<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find, Func<IElement, Task> raise)
        where TComponent : IComponent
    {
        for (var attempt = 1; ; attempt++)
        {
            Settle(cut);

            Task raised;

            try
            {
                raised = raise(find(cut));
            }
            catch (UnknownEventHandlerIdException) when (attempt < Attempts)
            {
                continue;
            }

            Settle(cut);

            if (!raised.IsFaulted)
                return;

            var failure = raised.Exception.Flatten().InnerExceptions;

            if (attempt < Attempts && failure.Any(e => e is UnknownEventHandlerIdException))
                continue;

            ExceptionDispatchInfo.Throw(failure[0]);
        }
    }

    public static void Raise<TComponent>(this IRenderedComponent<TComponent> cut, string selector, Func<IElement, Task> raise)
        where TComponent : IComponent
    {
        cut.Raise(c => c.Find(selector), raise);
    }

    public static void Click<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
    {
        cut.Raise(selector, ClickOn);
    }

    public static void Click<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find)
        where TComponent : IComponent
    {
        cut.Raise(find, ClickOn);
    }

    public static void DoubleClick<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
    {
        cut.Raise(selector, e => e.DoubleClickAsync(new MouseEventArgs { Detail = 2 }));
    }

    public static void MouseDown<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
    {
        cut.Raise(selector, e => e.MouseDownAsync(new MouseEventArgs()));
    }

    public static void MouseEnter<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
    {
        cut.Raise(selector, e => e.MouseEnterAsync(new MouseEventArgs()));
    }

    public static void MouseLeave<TComponent>(this IRenderedComponent<TComponent> cut, string selector)
        where TComponent : IComponent
    {
        cut.Raise(selector, e => e.MouseLeaveAsync(new MouseEventArgs()));
    }

    public static void KeyDown<TComponent>(this IRenderedComponent<TComponent> cut, string selector, string key)
        where TComponent : IComponent
    {
        cut.KeyDown(selector, new KeyboardEventArgs { Key = key, Code = key });
    }

    public static void KeyDown<TComponent>(this IRenderedComponent<TComponent> cut, string selector, KeyboardEventArgs key)
        where TComponent : IComponent
    {
        cut.Raise(selector, e => e.KeyDownAsync(key));
    }

    public static void Change<TComponent>(this IRenderedComponent<TComponent> cut, string selector, object value)
        where TComponent : IComponent
    {
        cut.Change(c => c.Find(selector), value);
    }

    public static void Change<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find, object value)
        where TComponent : IComponent
    {
        cut.Raise(find, e => e.ChangeAsync(new ChangeEventArgs { Value = value }));
    }

    public static void Input<TComponent>(this IRenderedComponent<TComponent> cut, string selector, object value)
        where TComponent : IComponent
    {
        cut.Input(c => c.Find(selector), value);
    }

    public static void Input<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find, object value)
        where TComponent : IComponent
    {
        cut.Raise(find, e => e.InputAsync(new ChangeEventArgs { Value = value }));
    }

    //Waits until everything queued on the renderer has run: an empty call goes to the back of its queue. Not on the
    //renderer's own thread, where a check of a WaitForAssertion can run (one that opens the saved connections' menu, say):
    //there the call would wait for the very work it is part of.
    public static void Settle<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent
    {
        if (SynchronizationContext.Current?.GetType().Name == "RendererSynchronizationContext")
            return;

        cut.InvokeAsync(() => { }).Wait();
    }

    //A click as bUnit's Click() makes it: a single one, which AiChatWindow tells from a double by its detail.
    private static Task ClickOn(IElement element)
    {
        return element.ClickAsync(new MouseEventArgs { Detail = 1 });
    }
}
