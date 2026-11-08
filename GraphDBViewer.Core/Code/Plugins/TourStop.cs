using Microsoft.AspNetCore.Components;

namespace GraphDBViewerWeb.Code;

///<summary>
///One stop of a page's guided tour (GuidedTour, 2026-11-07): the part of the page it is about, by CSS selector, or null for
///the page as a whole, whose card sits in the middle; what its card says; and a picture on the card, or null for none.
///"{app}" in the title or the text is the host's name for the app (<see cref="ViewerHostOptions.AppName"/>).
///
///A page hands its stops to <see cref="ShellState.StartTourIfAsked"/> as it opens, or to <see cref="ShellState.RequestTour"/>
///at any time. A stop whose part is not on the page when the walk starts is left out, so a tour that starts and ends with
///a stop about the whole page always has a first and a last card to show.
///</summary>
///<param name="Target">The part, by CSS selector, as <c>document.querySelectorAll</c> takes it; null for the whole page.</param>
///<param name="Title">The card's heading.</param>
///<param name="Text">The card's words.</param>
///<param name="Picture">
///Drawn on the card between the two: an <c>&lt;img&gt;</c>, or an <c>&lt;svg&gt;</c> in the theme's colors as the viewer's
///own are (TourPicture). Either is laid out at the card's width.
///</param>
public sealed record TourStop(string? Target, string Title, string Text, RenderFragment? Picture = null);
