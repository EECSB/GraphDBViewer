namespace GraphDBViewerWeb.Code;

///<summary>
///A browser-side <see cref="IAppStorage"/> that can be emptied of everything the app put in it.
///
///The mirror <see cref="FallbackAppStorage"/> keeps is one browser's copy of one account's workspace, and
///when a different account signs in on that browser the copy has to go before anything reads it: the
///server answers "nothing stored" to the new account, and until 2026-09-18 that meant the old account's
///copy was adopted as theirs and pushed up under their name (the 2026-09-17 audit, reproduced end to end).
///</summary>
public interface IMirrorStorage : IAppStorage
{
    ///<summary>Removes every value the app stored here. The adapter's own bookkeeping is kept.</summary>
    Task ClearAsync();
}
