﻿namespace TickSpec

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open TickSpec.LineParser
open TickSpec.FeatureParser
open TickSpec.ScenarioRun

/// Encapsulates step definitions for execution against features
type StepDefinitions (givens,whens,thens,events,valueParsers) =
    /// Returns method's step attribute or null
    static let GetStepAttributes (m:MemberInfo) = 
        Attribute.GetCustomAttributes(m,typeof<StepAttribute>)
    static let IsMethodInScope tags (scopedTags,_,m) =
        match scopedTags with
        | [] -> true            
        | xs -> xs |> List.exists (fun tag -> tags |> Seq.exists ((=) tag))
    /// Chooses matching definitions for specifed text
    let chooseDefinitions tags text definitions =  
        let chooseDefinition pattern =
            let r = Regex.Match(text,pattern)
            if r.Success then Some r else None
        definitions
        |> List.filter (fun (_,m) -> m |> IsMethodInScope tags)
        |> List.choose (fun (pattern:string,(_,_,m):string list * string list * MethodInfo) ->
            chooseDefinition pattern |> Option.map (fun r -> r,m)
        )
    /// Chooses defininitons for specified step and text
    let matchStep tags = function
        | GivenStep text -> chooseDefinitions tags text givens
        | WhenStep text -> chooseDefinitions tags text whens
        | ThenStep text -> chooseDefinitions tags text thens
    /// Extract arguments from specified match
    let extractArgs (r:Match) =        
        let args = List<string>()
        for i = 1 to r.Groups.Count-1 do
            r.Groups.[i].Value |> args.Add
        args.ToArray()      
    /// Resolves line
    let resolveLine (scenario:ScenarioSource) (step,line) =
        let matches = matchStep scenario.Tags step
        let fail e =
            let m = sprintf "%s on line %d" e line.Number
            StepException(m,line.Number,scenario.Name) |> raise
        if matches.IsEmpty then fail "Missing step definition"
        if matches.Length > 1 then fail "Ambiguous step definition"
        let r,m = matches.Head
        if m.ReturnType <> typeof<Void> then 
            fail "Step methods must return void/unit"
        if m.ContainsGenericParameters then
            fail "Step methods cannot be generic"
        let tableCount = line.Table |> Option.count
        let bulletsCount = line.Bullets |> Option.count
        let argCount = r.Groups.Count-1+tableCount+bulletsCount
        if m.GetParameters().Length <> argCount then
            fail "Parameter count mismatch"
        line,m,extractArgs r
    /// Chooses in scope events
    let chooseInScopeEvents (scenario:ScenarioSource) = 
        let choose xs =
            xs 
            |> Seq.filter (fun m -> m |> IsMethodInScope scenario.Tags)
            |> Seq.map (fun (_,_,e) -> e) 
        events 
        |> fun (ea,eb,ec,ed) -> choose ea, choose eb, choose ec, choose ed
    /// Gets description as scenario lines
    let getDescription steps =
            steps 
            |> Seq.map (fun (_,line) -> line.Text)
            |> String.concat "\r\n"
    new () =
        StepDefinitions(Assembly.GetCallingAssembly())
    /// Constructs instance by reflecting against specified assembly
    new (assembly:Assembly) =
        StepDefinitions(assembly.GetTypes())
    /// Constructs instance by reflecting against specified types
    new (types:Type[]) =
        let getScope attributes =
            attributes
            |> Seq.cast
            |> Seq.fold (fun (tags,features) (x:StepScopeAttribute) -> 
                x.Tag::tags, x.Feature::features
            ) ([],[])
        let methods = 
            types 
            |> Seq.collect (fun t -> 
                let attributes = t.GetCustomAttributes(typeof<StepScopeAttribute>,true)
                let tags, features = getScope attributes
                t.GetMethods()
                |> Seq.map (fun m ->
                    let attributes = m.GetCustomAttributes(typeof<StepScopeAttribute>,true)
                    let tags', features' = getScope attributes
                    tags@tags', features@features', m
                )
            )
        StepDefinitions(methods)
    internal new (methods:(string list * string list * MethodInfo) seq) =
        /// Step methods
        let givens, whens, thens =
            methods 
            |> Seq.map (fun ((_,_,m) as sm) -> sm, GetStepAttributes m)
            |> Seq.filter (fun (m,ca) -> ca.Length > 0)
            |> Seq.collect (fun ((_,_,m) as sm,ca) -> 
                ca 
                |> Array.map (fun a -> 
                    let p = 
                        match (a :?> StepAttribute).Step with
                        | null -> m.Name
                        | step -> step
                    p,a,sm
                )
                |> Seq.distinctBy (fun (p,a,m) -> p)
            )           
            |> Seq.fold (fun (gs,ws,ts) (p,a,m) -> 
                match a with
                | :? GivenAttribute -> ((p,m)::gs,ws,ts)
                | :? WhenAttribute -> (gs,(p,m)::ws,ts)
                | :? ThenAttribute -> (gs,ws,(p,m)::ts)
                | _ -> invalidOp "Unhandled StepAttribute"
            ) ([],[],[])
        
        let filter (t:Type) (elements:(string list * string list * MethodInfo) seq) =
            elements |> Seq.filter (fun (_,_,m) -> null <> Attribute.GetCustomAttribute(m,t))
        /// Step events
        let events = methods |> filter typeof<EventAttribute>
        let beforeScenario = events |> filter typeof<BeforeScenarioAttribute>
        let afterScenario = events |> filter typeof<AfterScenarioAttribute>
        let beforeStep = events |> filter typeof<BeforeStepAttribute>
        let afterStep = events |> filter typeof<AfterStepAttribute>
        let events = beforeScenario, afterScenario, beforeStep, afterStep
        /// Parser methods
        let valueParsers =
            methods 
            |> filter typeof<ParserAttribute>
            |> Seq.map (fun (_,_,m) -> m.ReturnType, m)
            |> Dict.ofSeq
        StepDefinitions(givens,whens,thens,events,valueParsers)
    /// Generate scenarios from specified lines (source undefined)
    member this.GenerateScenarios (lines:string []) =
        let featureSource = parseFeature lines
        featureSource.Scenarios
        |> Seq.map (fun scenario ->
            let steps = 
                scenario.Steps
                |> Seq.map (resolveLine scenario)
                |> Seq.toArray
            let events = chooseInScopeEvents scenario
            let action = generate events valueParsers (scenario.Name,steps)
            {Name=scenario.Name;Description=getDescription scenario.Steps;
             Action=TickSpec.Action(action);Parameters=scenario.Parameters;Tags=scenario.Tags}
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
        let featureSource = parseFeature lines
        let gen = FeatureGen(featureSource.Name,sourceUrl)
        let genType scenario =
            let lines = 
                scenario.Steps
                |> Seq.map (resolveLine scenario)
                |> Seq.toArray
            let events = chooseInScopeEvents scenario
            gen.GenScenario 
                events
                valueParsers
                (scenario.Name, lines, scenario.Parameters)
        let createAction scenario =
            let t = lazy (genType scenario)
            TickSpec.Action(fun () ->
                let instance = t.Force() |> Activator.CreateInstance
                let mi = instance.GetType().GetMethod("Run")
                mi.Invoke(instance,[||]) |> ignore
            )      
        let scenarios = 
            featureSource.Scenarios
            |> Seq.map (fun scenario ->
                let action = createAction scenario
                { Name=scenario.Name;Description=getDescription scenario.Steps;
                  Action=action;Parameters=scenario.Parameters;Tags=scenario.Tags}
            )
        { Name = featureSource.Name;
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