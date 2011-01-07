﻿namespace TickSpec

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open TickSpec.LineParser
open TickSpec.Parser
open TickSpec.ScenarioRun

/// Encapsulates Gherkin feature
type Feature = { 
    Name : string; 
    Source : string;
    Assembly : Assembly; 
    Scenarios : Scenario seq
    }

/// Encapsulates step definitions for execution against features
type StepDefinitions (methods:MethodInfo seq) =
    /// Returns method's step attribute or null
    static let GetStepAttributes (m:MemberInfo) = 
        Attribute.GetCustomAttributes(m,typeof<StepAttribute>)
    /// Step methods
    let givens, whens, thens =
        methods 
        |> Seq.filter (fun m -> (GetStepAttributes m).Length > 0)
        |> Seq.fold (fun (gs,ws,ts) m ->
            match (GetStepAttributes m).[0] with
            | :? GivenAttribute -> (m::gs,ws,ts)
            | :? WhenAttribute -> (gs,m::ws,ts)
            | :? ThenAttribute -> (gs,ws,m::ts)
            | _ -> invalidOp "Unhandled StepAttribute"
        ) ([],[],[])    
    /// Parser methods
    let parsers =
        methods 
        |> Seq.filter (fun m -> null <> Attribute.GetCustomAttribute(m,typeof<ParserAttribute>))
        |> Seq.map (fun m -> m.ReturnType, m)        
        |> Dict.ofSeq
    /// Chooses matching definitions for specifed text
    let chooseDefinitions text definitions =  
        let chooseDefinition pattern =
            let r = Regex.Match(text,pattern)
            if r.Success then Some r else None
        definitions |> List.choose (fun (m:MethodInfo) ->
            let steps = 
                Attribute.GetCustomAttributes(m,typeof<StepAttribute>)
                |> Array.map (fun a -> (a :?> StepAttribute).Step)
                |> Array.filter ((<>) null)
            match steps |> Array.tryPick chooseDefinition with
            | Some r -> Some r
            | None -> chooseDefinition m.Name
            |> Option.map (fun r -> r,m)
        )
    /// Chooses defininitons for specified step and text
    let matchStep = function
        | Given text -> chooseDefinitions text givens
        | When text -> chooseDefinitions text whens
        | Then text -> chooseDefinitions text thens
    /// Extract arguments from specified match
    let extractArgs (r:Match) =        
        let args = List<string>()
        for i = 1 to r.Groups.Count-1 do
            r.Groups.[i].Value |> args.Add
        args.ToArray()      
    /// Computes combinations of table values
    let computeCombinations (tables:Table []) =
        let values = 
            tables 
            |> Seq.map (fun table ->
                table.Rows |> Array.map (fun row ->
                    row
                    |> Array.mapi (fun i col ->
                        table.Header.[i],col
                    )
                )
            )
            |> Seq.toList
        values |> List.combinations
    /// Replace line with specified named values
    let replaceLine (xs:seq<string * string>) (scenario,n,tags,line,step) =
        let replace s =
            let lookup (m:Match) =
                let x = m.Value.TrimStart([|'<'|]).TrimEnd([|'>'|])
                xs |> Seq.tryFind (fun (k,_) -> k = x)
                |> (function Some(_,v) -> v | None -> m.Value)
            let pattern = "<([^<]*)>"
            Regex.Replace(s, pattern, lookup)
        let step = 
            match step with
            | Given s -> replace s |> Given
            | When s -> replace s |> When
            | Then s  -> replace s |> Then
        let table =
            line.Table 
            |> Option.map (fun table ->
                Table(table.Header,
                    table.Rows |> Array.map (fun row ->
                        row |> Array.map (fun col -> replace col)
                    )
                )
            )
        let bullets =
            line.Bullets
            |> Option.map (fun bullets -> bullets |> Array.map replace)                                  
        (scenario,n,tags,{line with Table=table;Bullets=bullets},step)
    /// Resolves line
    let resolveLine (scenario,_,_,line,step) =
        let matches = matchStep step
        let fail e =
            let m = sprintf "%s on line %d" e line.Number 
            StepException(m,line.Number,scenario.ToString()) |> raise
        if matches.IsEmpty then fail "Missing step definition"
        if matches.Length > 1 then fail "Ambiguous step definition"
        let r,m = matches.Head
        if m.ReturnType <> typeof<Void> then 
            fail "Step methods must return void/unit"
        let tableCount = line.Table |> Option.count
        let bulletsCount = line.Bullets |> Option.count
        let argCount = r.Groups.Count-1+tableCount+bulletsCount
        if m.GetParameters().Length <> argCount then
            fail "Parameter count mismatch"
        line,m,extractArgs r
    /// Gets description as scenario lines
    let getDescription steps =
            steps 
            |> Seq.map (fun (line,_,_) -> line.Text)
            |> String.concat "\r\n"
    /// Appends shared examples to scenarios as examples
    let appendSharedExamples (sharedExamples:Table[]) scenarios  =
        if Seq.length sharedExamples = 0 then
            scenarios
        else
            scenarios |> Seq.map (function 
                | scenarioName,tags,steps,None ->
                    scenarioName,tags,steps,Some(sharedExamples)
                | scenarioName,tags,steps,Some(exampleTables) ->
                    scenarioName,tags,steps,Some(Array.append exampleTables sharedExamples)
            )
    /// Parses lines of feature
    let parseFeature (lines:string[]) =
        let featureName,background,scenarios,sharedExamples = parseBlocks lines     
        featureName,
            scenarios 
            |> appendSharedExamples sharedExamples
            |> Seq.collect (function
                | name,tags,steps,None ->
                    let steps = Seq.append background steps
                    Seq.singleton
                        (name, tags, steps, [||])
                | name,tags,steps,Some(exampleTables) ->            
                    /// All combinations of tables
                    let combinations = computeCombinations exampleTables
                    // Execute each combination
                    combinations |> Seq.mapi (fun i combination ->
                        let name = sprintf "%s(%d)" name i
                        let combination = Seq.concat combination |> Seq.toArray
                        let steps = 
                            Seq.append background steps
                            |> Seq.map (replaceLine combination)                                          
                        name, tags, steps, combination
                    )
            )
    /// Constructs instance by reflecting against specified types
    new (types:Type[]) =
        let methods = 
            types 
            |> Seq.collect (fun t -> t.GetMethods())
        StepDefinitions(methods)
    /// Constructs instance by reflecting against specified assembly
    new (assembly:Assembly) =
        StepDefinitions(assembly.GetTypes())
    new () =
        StepDefinitions(Assembly.GetCallingAssembly())
    /// Generate scenarios from specified lines (source undefined)
    member this.GenerateScenarios (lines:string []) =
        let _, scenarioBlocks = parseFeature lines
        scenarioBlocks
        |> Seq.map (fun (name, tags, steps, parameters) ->
            let steps = steps |> Seq.map resolveLine
            let action = generate parsers (name,steps)
            {Name=name;Description=getDescription steps;
             Action=TickSpec.Action(action);Parameters=parameters;Tags=tags}
        )
    member this.GenerateScenarios (reader:TextReader) =
        this.GenerateScenarios(TextReader.readAllLines reader)
    member this.GenerateScenarios (feature:System.IO.Stream) =
        use reader = new StreamReader(feature)
        this.GenerateScenarios(reader)
    /// Execute step definitions in specified lines (source undefined)
    member this.Execute (lines:string[]) =
        this.GenerateScenarios lines
        |> Seq.iter (fun scenario -> scenario.Action.Invoke())
    member this.Execute (reader:TextReader) =
        this.Execute(TextReader.readAllLines reader)
    member this.Execute (feature:System.IO.Stream) =
        use reader = new StreamReader(feature)
        this.Execute (reader)
    /// Generates feature in specified lines from source document
    member this.GenerateFeature (sourceUrl:string,lines:string[]) =
        let featureName, scenarioBlocks = parseFeature lines
        let gen = FeatureGen(featureName,sourceUrl)
        let createAction (scenarioName, lines, ps) =
            let t = gen.GenScenario parsers (scenarioName, lines, ps)
            let instance = Activator.CreateInstance t
            let mi = instance.GetType().GetMethod("Run")
            TickSpec.Action(fun () -> mi.Invoke(instance,[||]) |> ignore)      
        let scenarios = 
            scenarioBlocks
            |> Seq.map (fun (name, tags, lines, parameters) ->
                let steps = lines |> Seq.map resolveLine |> Seq.toArray           
                let action = createAction (name, steps, parameters)           
                { Name=name;Description=getDescription steps;
                      Action=action;Parameters=parameters;Tags=tags}
            )
        { Name = featureName; 
          Source = sourceUrl;
          Assembly = gen.Assembly; 
          Scenarios = scenarios |> Seq.toArray 
        }
    member this.GenerateFeature (sourceUrl:string,reader:TextReader) =
        this.GenerateFeature(sourceUrl, TextReader.readAllLines reader)
    member this.GenerateFeature (sourceUrl:string,feature:System.IO.Stream) =
        use reader = new StreamReader(feature)
        this.GenerateFeature(sourceUrl, reader)
#if SILVERLIGHT
#else   
    member this.GenerateFeature (path:string) =
        this.GenerateFeature(path,File.ReadAllLines(path))
#endif
    /// Generates scenarios in specified lines from source document
    member this.GenerateScenarios (sourceUrl:string,lines:string[]) =
        this.GenerateFeature(sourceUrl,lines).Scenarios    
    member this.GenerateScenarios (sourceUrl:string,reader:TextReader) =
        this.GenerateScenarios(sourceUrl, TextReader.readAllLines reader)
    member this.GenerateScenarios (sourceUrl:string,feature:System.IO.Stream) =
        use reader = new StreamReader(feature)
        this.GenerateScenarios(sourceUrl, reader)
#if SILVERLIGHT
#else 
    member this.GenerateScenarios (path:string) =
        this.GenerateScenarios(path,File.ReadAllLines(path))
#endif    
    /// Executes step definitions in specified lines from source document
    member this.Execute (sourceUrl:string,lines:string[]) =
        let scenarios = this.GenerateScenarios(sourceUrl,lines)
        scenarios |> Seq.iter (fun action -> action.Action.Invoke())
    member this.Execute (sourceUrl:string,reader:TextReader) =
        this.Execute(sourceUrl, TextReader.readAllLines reader)
    member this.Execute (sourceUrl:string,feature:System.IO.Stream) =
        use reader = new StreamReader(feature)
        this.Execute (sourceUrl,reader)
#if SILVERLIGHT
#else 
    member this.Execute (path:string) =
        this.Execute(path,File.ReadAllLines(path))
#endif