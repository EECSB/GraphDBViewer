namespace GraphDBViewerWeb.Code;

///<summary>
///A secret saved with a connection, a password or an API key, kept to the transport it was saved with: the user's rule
///of 2026-10-03, that a stored secret is never sent in plain text without somebody entering it again.
///
///Plain is anybody's choice and is never refused. But a form that holds a saved connection, secret and all, sends the
///secret with whatever the form says next, so turning the connection's encryption off (the SSL switch, an http address)
///would send and save the saved secret unencrypted with nobody having typed it. So the moment the form's connection
///stops being encrypted, the saved secret leaves the form; it comes back if the connection is encrypted again before
///anybody has typed another. A secret typed meanwhile is the person's own entry and is left alone, and a connection
///saved without encryption keeps its secret, since somebody chose that.
///
///The connection card, the instance graph's settings page and the AI model form each keep one, told what was loaded or
///saved, and asked again after every field that decides the transport.
///</summary>
public sealed class SavedSecretRule
{
    private string saved;
    private bool savedEncrypted;

    ///<summary>True while the saved secret is out of the form because the connection is no longer encrypted.</summary>
    public bool HeldBack { get; private set; }

    ///<summary>What the form was loaded from, or just saved as: its secret, and whether its connection was encrypted.</summary>
    public void Remember(string secret, bool encrypted)
    {
        saved = secret;
        savedEncrypted = encrypted;
        HeldBack = false;
    }

    ///<summary>The secret the form should hold, given the one it holds and whether its connection is now encrypted.</summary>
    public string Keep(string current, bool encrypted)
    {
        if (string.IsNullOrEmpty(saved) || !savedEncrypted)
            return current;

        if (!encrypted && !HeldBack && current == saved)
        {
            HeldBack = true;

            return null;
        }
        else if (encrypted && HeldBack)
        {
            HeldBack = false;

            if (string.IsNullOrEmpty(current))
                return saved;
        }

        return current;
    }

    ///<summary>Whether the form should say why its secret's field is empty: held back, and nothing typed since.</summary>
    public bool Says(string current)
    {
        return HeldBack && string.IsNullOrEmpty(current);
    }

    ///<summary>A database connection, as loaded or saved; null when the form holds nothing saved.</summary>
    public void Remember(GremlinDB.GremlinConnection connection)
    {
        if (connection == null)
            Remember(null, false);
        else
            Remember(connection.AuthKey, GraphDbProviders.For(connection.DatabaseType).Encrypts(connection));
    }

    ///<summary>Applies the rule to a database connection's password, read the way its provider says it is encrypted.</summary>
    public void Keep(GremlinDB.GremlinConnection connection)
    {
        if (connection == null)
            return;

        connection.AuthKey = Keep(connection.AuthKey, GraphDbProviders.For(connection.DatabaseType).Encrypts(connection));
    }

    ///<summary>Whether the form should say why a database connection's password field is empty.</summary>
    public bool Says(GremlinDB.GremlinConnection connection)
    {
        return Says(connection?.AuthKey);
    }
}
