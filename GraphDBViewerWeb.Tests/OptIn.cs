using System.Globalization;
using System.Net.Sockets;

namespace GraphDBViewerWeb.Tests;

///<summary>
///A fact that needs what an ordinary run does not have, a server of its own or minutes of measuring, and is skipped
///unless somebody asks for it by its variable. Each argument is a variable that must be set, or <c>NAME=value</c> for one
///that must hold exactly that value, and the skip reason names whichever do not.
///
///Skipped rather than passed: until 2026-10-23 each of these returned at once without its variable and was counted
///among the passes, so a run that had tested none of them said it had. Asked for, a test checks that its server
///answers (<see cref="OptIn.ServerAsync"/>) and fails, naming the address and the variable, when it does not: an
///opted-in run against nothing is a failure, not a pass.
///</summary>
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(params string[] variables)
    {
        Skip = OptIn.Reason(variables);
    }
}

///<summary>The theory form of <see cref="OptInFactAttribute"/>. Skipped, a theory is one test, however many rows it has.</summary>
public sealed class OptInTheoryAttribute : TheoryAttribute
{
    public OptInTheoryAttribute(params string[] variables)
    {
        Skip = OptIn.Reason(variables);
    }
}

///<summary>What the opt-in attributes ask of the environment, and the check a test that was asked for makes of its server.</summary>
public static class OptIn
{
    ///<summary>How long a server has to take a connection before a test that was asked for fails.</summary>
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(10);

    ///<summary>Null when every variable holds what it must, and otherwise the skip reason, naming the ones that do not.</summary>
    public static string? Reason(params string[] variables)
    {
        var unset = variables.Where(variable => !Holds(variable)).ToList();

        if (unset.Count == 0)
            return null;

        return $"Opt-in: set {string.Join(" and ", unset)} to run it.";
    }

    ///<summary>Whether a variable is set, or for <c>NAME=value</c>, whether it holds exactly that value.</summary>
    private static bool Holds(string variable)
    {
        var equals = variable.IndexOf('=');

        if (equals < 0)
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable));

        return Environment.GetEnvironmentVariable(variable.Substring(0, equals)) == variable.Substring(equals + 1);
    }

    ///<summary>
    ///The server a variable names, <c>host</c> or <c>host:port</c>, once something there takes a connection. When nothing
    ///does, the test fails with a sentence naming the address and the variable.
    ///</summary>
    public static async Task<(string Host, int Port)> ServerAsync(string variable, int defaultPort = 8182)
    {
        var (host, port) = Address(Environment.GetEnvironmentVariable(variable) ?? "", defaultPort);

        await AnswersAsync(variable, host, port);

        return (host, port);
    }

    ///<summary><c>host</c> or <c>host:port</c> as a host and a port, the port <paramref name="defaultPort"/> when it names none.</summary>
    public static (string Host, int Port) Address(string target, int defaultPort)
    {
        var colon = target.LastIndexOf(':');

        if (colon < 0)
            return (target.Trim(), defaultPort);

        return (target.Substring(0, colon).Trim(), int.Parse(target.Substring(colon + 1), CultureInfo.InvariantCulture));
    }

    ///<summary>Fails the test, naming the address and the variable that gave it, unless something there takes a connection.</summary>
    public static async Task AnswersAsync(string variable, string host, int port)
    {
        var refusal = await RefusalAsync(host, port);

        if (refusal != null)
            Assert.Fail($"{variable} names {host}:{port}, and nothing answered there: {refusal}.");
    }

    ///<summary>Null when something at host:port takes a connection within <see cref="AnswerWithin"/>, and otherwise why not.</summary>
    public static async Task<string?> RefusalAsync(string host, int port)
    {
        using var client = new TcpClient();
        using var waited = new CancellationTokenSource(AnswerWithin);

        try
        {
            await client.ConnectAsync(host, port, waited.Token);

            return null;
        }
        catch (SocketException refused)
        {
            return refused.Message.TrimEnd('.');
        }
        catch (OperationCanceledException)
        {
            return $"no connection was taken within {AnswerWithin.TotalSeconds:F0} s";
        }
    }
}

///<summary>
///The Gremlin server the live-server tests walk, <c>GremlinIntegrationTests</c>, <c>SeamBatchLiveTests</c> and
///<c>DeleteRefusalLiveTests</c>: <c>GREMLIN_HOST</c>, which has no default, <c>GREMLIN_PORT</c> (default 8182) and
///<c>GREMLIN_TRANSPORT</c> (default WebSocket). They are opt-in like every other probe,
///<c>[OptInFact(LiveGremlin.HostVariable)]</c>: skipped unless <c>GREMLIN_HOST</c> is set, and once it is, failed with
///a sentence naming the address and the variables when nothing answers there.
///
///<strong>They write to the server they are given.</strong> <c>GremlinIntegrationTests</c> seeds a small graph of its
///own and leaves it there for the next run, and the other two make records and drop them again. Point
///<c>GREMLIN_HOST</c> at a throwaway server, never at the development graph (<c>treeality-dev-graph</c>,
///192.168.1.7:8186).
///
///Until 2026-10-23 they were the other way round: they ran unless <c>SKIP_GREMLIN_TESTS=true</c>, against
///192.168.1.5:8182 when no host was named, an in-memory TinkerGraph that no longer exists, so every run had to say
///there was no server, and one that did not failed all seventeen.
///</summary>
public static class LiveGremlin
{
    public const string HostVariable = "GREMLIN_HOST";

    public static string Host => Environment.GetEnvironmentVariable(HostVariable) ?? "";

    public static int Port
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("GREMLIN_PORT"), out var port))
                return port;

            return 8182;
        }
    }

    public static string Transport => Environment.GetEnvironmentVariable("GREMLIN_TRANSPORT") ?? "WebSocket";

    ///<summary>Fails the test, naming the address, its variables and the way to skip, unless the server takes a connection.</summary>
    public static async Task AnswersAsync()
    {
        var refusal = await OptIn.RefusalAsync(Host, Port);

        if (refusal != null)
            Assert.Fail($"{HostVariable} and GREMLIN_PORT name {Host}:{Port}, and nothing answered there: {refusal}. Point them at a throwaway Gremlin server, never the development graph, which these tests write to, or unset {HostVariable} to skip them.");
    }
}
