using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///Curated Cypher example queries shown in the "Examples" tab when the editor language is openCypher —
///the Neo4j / Memgraph counterpart to <see cref="GremlinExamples"/>. Clicking one pastes its
///<see cref="QueryExample.Query"/> into the editor. The sample-graph loaders create their data and end
///with <c>RETURN *</c>, so the new subgraph draws immediately.
///</summary>
public static class CypherExamples
{
    ///<summary>
    ///A table-manufacturing assembly tree (13 nodes, 15 relationships): a 2x4 is cut into 4 legs, the legs
    ///+ table top + 4 screws compose an unpainted table, and paint + the unpainted table compose the
    ///finished table. Relationships are named so <c>RETURN *</c> draws them.
    ///</summary>
    public const string TableGraphLoader = @"CREATE (lumber:Material {name:'2x4 Lumber', description:'2x4 pine board'}),
       (leg1:Component {name:'Leg 1', description:'Table leg cut from 2x4'}),
       (leg2:Component {name:'Leg 2', description:'Table leg cut from 2x4'}),
       (leg3:Component {name:'Leg 3', description:'Table leg cut from 2x4'}),
       (leg4:Component {name:'Leg 4', description:'Table leg cut from 2x4'}),
       (top:Component {name:'Table Top', description:'Flat table surface'}),
       (screw1:Component {name:'Screw 1', description:'Wood screw'}),
       (screw2:Component {name:'Screw 2', description:'Wood screw'}),
       (screw3:Component {name:'Screw 3', description:'Wood screw'}),
       (screw4:Component {name:'Screw 4', description:'Wood screw'}),
       (paint:Material {name:'Paint', description:'Finish coat'}),
       (unpainted:Assembly {name:'Unpainted Table', description:'Assembled but unpainted table'}),
       (finished:Product {name:'Finished Table', description:'Completed painted table'}),
       (lumber)-[e1:cutInto]->(leg1),
       (lumber)-[e2:cutInto]->(leg2),
       (lumber)-[e3:cutInto]->(leg3),
       (lumber)-[e4:cutInto]->(leg4),
       (leg1)-[e5:composes]->(unpainted),
       (leg2)-[e6:composes]->(unpainted),
       (leg3)-[e7:composes]->(unpainted),
       (leg4)-[e8:composes]->(unpainted),
       (top)-[e9:composes]->(unpainted),
       (screw1)-[e10:composes]->(unpainted),
       (screw2)-[e11:composes]->(unpainted),
       (screw3)-[e12:composes]->(unpainted),
       (screw4)-[e13:composes]->(unpainted),
       (unpainted)-[e14:composes]->(finished),
       (paint)-[e15:composes]->(finished)
RETURN *";

    ///<summary>
    ///A social network: ten people in four cities, joined by how they actually know one another —
    ///friends, colleagues, a married couple, and who mentors whom (10 nodes, 19 relationships).
    ///People only; the Gremlin copy of this explains why, and the two are kept in step.
    ///</summary>
    public const string ModernGraphLoader = @"CREATE (alice:person {name:'Alice', age:34, city:'Seattle'}),
       (ben:person {name:'Ben', age:41, city:'Seattle'}),
       (grace:person {name:'Grace', age:26, city:'Seattle'}),
       (carla:person {name:'Carla', age:29, city:'Portland'}),
       (daniel:person {name:'Daniel', age:37, city:'Portland'}),
       (jonas:person {name:'Jonas', age:47, city:'Portland'}),
       (elena:person {name:'Elena', age:45, city:'San Francisco'}),
       (frank:person {name:'Frank', age:52, city:'San Francisco'}),
       (hugo:person {name:'Hugo', age:31, city:'Denver'}),
       (iris:person {name:'Iris', age:38, city:'Denver'}),
       (alice)-[k1:knows {since:2015}]->(ben),
       (alice)-[k2:knows {since:2019}]->(grace),
       (ben)-[k3:knows {since:2019}]->(grace),
       (carla)-[k4:knows {since:2016}]->(daniel),
       (daniel)-[k5:knows {since:2012}]->(jonas),
       (carla)-[k6:knows {since:2021}]->(jonas),
       (hugo)-[k7:knows {since:2017}]->(iris),
       (frank)-[k8:knows {since:2008}]->(jonas),
       (grace)-[k9:knows {since:2022}]->(hugo),
       (daniel)-[k10:knows {since:2014}]->(elena),
       (iris)-[k11:knows {since:2013}]->(alice),
       (jonas)-[k12:knows {since:2011}]->(ben),
       (alice)-[w1:worksWith {at:'Northwind Studio'}]->(carla),
       (elena)-[w2:worksWith {at:'Cascade Labs'}]->(iris),
       (elena)-[m1:marriedTo {since:2006}]->(frank),
       (ben)-[t1:mentors {field:'engineering'}]->(grace),
       (jonas)-[t2:mentors {field:'architecture'}]->(carla),
       (iris)-[t3:mentors {field:'photography'}]->(hugo),
       (frank)-[t4:mentors {field:'law'}]->(daniel)
RETURN *";

    ///<summary>A small, cyclic flight-route network between west-coast cities (5 nodes, 6 relationships).</summary>
    public const string FlightRoutesLoader = @"CREATE (sea:City {name:'Seattle', code:'SEA'}),
       (pdx:City {name:'Portland', code:'PDX'}),
       (sfo:City {name:'San Francisco', code:'SFO'}),
       (lax:City {name:'Los Angeles', code:'LAX'}),
       (den:City {name:'Denver', code:'DEN'}),
       (sea)-[r1:route {miles:130}]->(pdx),
       (pdx)-[r2:route {miles:535}]->(sfo),
       (sfo)-[r3:route {miles:337}]->(lax),
       (sea)-[r4:route {miles:1024}]->(den),
       (den)-[r5:route {miles:862}]->(lax),
       (sfo)-[r6:route {miles:679}]->(sea)
RETURN *";

    ///<summary>
    ///Three mechanical parts, each carrying a linked image (gdbvImage) and 3D model (gdbvModel) set to show
    ///(gdbvShow), joined to a central mount — the image draws in 2D, the .obj model in 3D. The files are
    ///hosted externally, so the viewer needs internet to load them.
    ///</summary>
    public const string ThreeDObjectsLoader = @"CREATE (screw:Component {name:'Screw', quantity:3, gdbvImage:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/screw.png', gdbvModel:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/screw.obj', gdbvShow:'gdbvImage,gdbvModel', exampleType:'3d_example_1'}),
       (gear:Component {name:'Gear', quantity:3, gdbvImage:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/gear.jpg', gdbvModel:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/gear.obj', gdbvShow:'gdbvImage,gdbvModel', exampleType:'3d_example_1'}),
       (mount:Component {name:'Mount', quantity:1, gdbvImage:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/mount.jpg', gdbvModel:'https://eecs.blog/BlazorApps/GraphDBExampleFiles/mount.obj', gdbvShow:'gdbvImage,gdbvModel', exampleType:'3d_example_1'}),
       (screw)-[r1:`edge label`]->(mount),
       (gear)-[r2:`edge label`]->(mount)
RETURN *";

    ///<summary>All example groups, in display order — mirrors <see cref="GremlinExamples.Groups"/>.</summary>
    public static IReadOnlyList<QueryExampleGroup> Groups { get; } = new List<QueryExampleGroup>
    {
        new("Inspect", new QueryExample[]
        {
            new("Count nodes",         "MATCH (n) RETURN count(n)"),
            new("Count relationships", "MATCH ()-[r]->() RETURN count(r)"),
            new("All nodes",           "MATCH (n) RETURN n"),
            new("Nodes + props",       "MATCH (n) RETURN labels(n) AS labels, properties(n) AS props"),
            new("All relationships",   "MATCH ()-[r]->() RETURN r"),
            new("Distinct labels",     "MATCH (n) UNWIND labels(n) AS label RETURN DISTINCT label"),
        }),

        new("Visualize", new QueryExample[]
        {
            new("Full graph",       "MATCH (n) OPTIONAL MATCH (n)-[r]->(m) RETURN n, r, m"),
            new("First 25 nodes",   "MATCH (n) RETURN n LIMIT 25"),
        }),

        new("Mutate", new QueryExample[]
        {
            new("Add Component", "CREATE (c:Component {name:'New Component', description:''}) RETURN c"),
            new("Drop ALL data", "MATCH (n) DETACH DELETE n", Destructive: true),
        }),

        new("Sample graphs", new QueryExample[]
        {
            new("Table assembly", TableGraphLoader),
            new("Social network", ModernGraphLoader),
            new("Flight routes",  FlightRoutesLoader),
            new("3D Objects",     ThreeDObjectsLoader),
        }),
    };
}
