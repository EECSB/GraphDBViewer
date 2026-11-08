using System;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Which server is on the other end of a Bolt connection. Neo4j and Memgraph both speak it, and the viewer reaches both
///through one engine, chosen with one button, so nothing in the connection says which it is: until 2026-10-31 a Memgraph
///connected saying "Connected to Neo4j.". Asked once the probe has answered, the way the Graph algorithms panel asks
///(<see cref="MageAlgorithms.ProceduresQuery"/>): Memgraph lists its procedures, with or without MAGE, and Neo4j has no
///<c>mg.procedures</c> and refuses it.
///</summary>
public static class BoltServer
{
    ///<summary>Memgraph's own procedure list, which is built into it; one row is enough to know it answered.</summary>
    public const string MemgraphQuery = "CALL mg.procedures() YIELD name RETURN name LIMIT 1";

    ///<summary>
    ///What connecting says when the server is Memgraph, or null to keep the engine's own "Connected to Neo4j.". Asked
    ///down the read-only path where the driver has one, as the algorithms panel asks it. A question that could not be
    ///asked keeps the engine's message: it says nothing about which server answered the probe.
    ///</summary>
    public static async Task<string?> ConnectedMessageAsync(IGraphDb db, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await DatabaseAlgorithms.ReaderOf(db)(MemgraphQuery, cancellationToken);

            if (answer.IsError)
                return null;

            return "Connected to Memgraph.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
