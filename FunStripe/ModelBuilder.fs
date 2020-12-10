#if INTERACTIVE
    #r "nuget: FSharp.Data";;
#else
namespace FunStripe
#endif

open FSharp.Data
open FSharp.Data.JsonExtensions
open System
open System.IO
open System.Linq
open System.Text.RegularExpressions

module ModelBuilder =

    type Property = {
        AnyOf: JsonValue array option
        Description: string option
        Enum: JsonValue array option
        Items: JsonValue option
        Nullable: bool option
        Properties: JsonValue
        Ref: string option
        Required: JsonValue array
        Type': string option
    }

    let commentify (s: string) = 
        s.Replace("\n", "\n\t///")

    let clean (s: string) =
        s.Replace("-", "").Replace(" ", "")

    let pascalCasify (s: string) =
        Regex.Replace(s, @"(^|_|\.)(\w)", fun (m: Match) -> m.Groups.[2].Value.ToUpper())

    let escapeForJson s =
        if Regex.IsMatch(s, @"^\p{Lu}") then
            $@"[<JsonUnionCase(""{s}"")>] {s |> clean |> pascalCasify}"
        else
            if Regex.IsMatch(s, @"^\d") then
                $@"[<JsonUnionCase(""{s}"")>] Numeric{s |> clean |> pascalCasify}"
            elif s.Contains("-") || s.Contains(" ") then
                $@"[<JsonUnionCase(""{s}"")>] {s |> clean |> pascalCasify}"
            else
                s |> clean |> pascalCasify

    let parseRef (s: string) =
        let m = Regex.Match(s, "/([^/]+)$")
        if m.Success then
            m.Groups.[1].Value
        else
            failwith $"Error: unparsable reference: {s}"

    let mapType (s: string) =
        match s with
        | "boolean" -> "bool"
        | "integer" -> "int"
        | "number" -> "decimal"
        | _ -> s

    let getProperties (jv: JsonValue) =
        {
            AnyOf = jv.TryGetProperty("anyOf") |> function | Some v -> v.AsArray() |> Some | None -> None
            Description = jv.TryGetProperty("description") |> function | Some v -> v.AsString() |> Some | None -> None
            Enum = jv.TryGetProperty("enum") |> function | Some v -> v.AsArray() |> Some | None -> None
            Items = jv.TryGetProperty("items") |> function | Some v -> v |> Some | None -> None
            Nullable = jv.TryGetProperty("nullable") |> function | Some v -> v.AsBoolean() |> Some | None -> None
            Properties = jv.TryGetProperty("properties") |> function | Some v -> v | None -> JsonValue.Null
            Ref = jv.TryGetProperty("$ref") |> function | Some v -> v.AsString() |> Some | None -> None
            Required = jv.TryGetProperty("required") |> function | Some v -> v.AsArray() | None -> [||]
            Type' = jv.TryGetProperty("type") |> function | Some v -> v.AsString() |> mapType |> Some | None -> None
        }

    let createEnum name (jvv: JsonValue array) =
        let s = 
            ("", 
                jvv
                |> Array.map(fun jv -> $"\t\t| {jv.AsString() |> escapeForJson}\n")
            ) |> String.Join
        $"\tand {name} =\n{s}"

    let createEnum2 name (ss: string seq) =
        let s = 
            ("", 
                ss
                |> Seq.map(fun s -> $"\t\t| {s |> escapeForJson}\n")
            ) |> String.Join
        $"\tand {name} =\n{s}"

    let parseStringEnum desc =
        let m = Regex.Match(desc, @"Can be `([^`]+)`(?:[^,]*?, `([^`]+)`)*[^,]*?,? or (?:`([^`]+)`|null).")
        if m.Success then
            m.Groups.Cast<Group>()
            |> Seq.skip 1
            |> Seq.collect(fun g -> g.Captures.Cast<Capture>())
            |> Seq.map(fun c -> c.Value)
            |> Some
        else
            None

    let createAnyOf name (jvv: JsonValue array) =
        let s =
            ("", 
                jvv
                |> Array.map(fun jv ->
                    let props = jv |> getProperties
                    match props.Type' with
                    | Some t ->
                        $"\t\t| {t |> escapeForJson} of {t}\n"
                    | _ ->
                    
                        match props.Ref with
                        | Some r ->
                            let refName = (r |> parseRef |> pascalCasify)
                            $"\t\t| {refName} of {refName}\n"
                        | None ->
                            ""
                )
            ) |> String.Join
        $"\tand {name} =\n{s}"

    let write s (sb: Text.StringBuilder) =
        sb.AppendLine s |> ignore

    let parseModel filePath =

        let root = __SOURCE_DIRECTORY__

        let filePath' = defaultArg filePath $@"{root}/res/spec3.sdk.json"
        let json = File.ReadAllText(filePath')

        let root = JsonValue.Parse json
        let components = root.Item "components"
        let schemas = components.Item "schemas"

        let sb = Text.StringBuilder()

        let mutable isFirstOccurrence = true

        sb |> write "namespace FunStripe\n\nopen FSharp.Json\n\nmodule StripeModel =\n"
        
        for (key, value) in schemas.Properties do
            let name = key |> pascalCasify
            let record = value |> getProperties

            match record.Description with
            | Some d ->
                sb |> write $"\t///{d |> commentify}"
            | None ->
                ()

            match record.AnyOf with
            | Some aoo ->
                sb |> write (createAnyOf name aoo)
            | None ->

                let keyword =
                    if isFirstOccurrence then
                        isFirstOccurrence <- false
                        "type"
                    else
                        "and"

                sb |> write $"\t{keyword} {name} = {{\n"
        
                let enums = Collections.Generic.List<string>()
                let anyOfs = Collections.Generic.List<string>()

                let properties = record.Properties
                let required = record.Required |> Array.map (fun jv -> jv.AsString())

                if properties.Properties |> Array.isEmpty then
                    sb |> write "\t\tEmptyProperties: string list\n"
                else
                    properties.Properties
                    |> Array.iter (fun (k1, v1) ->
                        let k1' = k1 |> pascalCasify
                        let props = v1 |> getProperties
        
                        //let opt = if required |> Array.exists (fun r -> r = k1) then "" else " option"
                        
                        //// to do: the following is nullable fields but the preceding is optional create params
                        let opt = props.Nullable |> function | Some true -> " option" | _ -> ""

                        match props.Description with
                        | Some d when (String.IsNullOrWhiteSpace d |> not) ->
                            sb |> write $"\t\t///{d |> commentify}"
                        | _ ->
                            ()
        
                        match props.Enum with
                        | Some ee ->
                            let enumName = $"{name}{k1'}"
                            sb |> write $"\t\t{k1'}: {enumName}{opt}\n"
                            enums.Add (createEnum enumName ee)
                        | None ->

                            match props.Description with
                            | Some d when (String.IsNullOrWhiteSpace d |> not) && (d.Contains("Can be `")) ->
                                match d |> parseStringEnum with
                                | Some ee ->
                                    let enumName = $"{name}{k1'}"
                                    sb |> write $"\t\t{k1'}: {enumName}{opt}\n"
                                    enums.Add (createEnum2 enumName ee)
                                | None ->
                                    ()
                            | _ ->
                                ()

                            match props.AnyOf with
                            | Some aoo ->
                                let anyOfName = $"{name}{k1'}DU"
                                sb |> write $"\t\t{k1'}: {anyOfName}{opt}\n"
                                anyOfs.Add (createAnyOf anyOfName aoo)
                            | None ->
        
                                match props.Type' with
                                | Some t when t = "array" ->
                                    match props.Items with
                                    | Some i ->
                                        let itemProps = i |> getProperties
                                        match itemProps.Ref with
                                        | Some r ->
                                            sb |> write $"\t\t{k1'}: {r |> parseRef |> pascalCasify} list\n"
                                        | None ->
                                            match itemProps.Enum with
                                            | Some ee ->
                                                let enumName = $"{name}{k1'}"
                                                sb |> write $"\t\t{k1'}: {enumName} list{opt}\n"
                                                enums.Add(createEnum enumName ee)
                                            | None ->
                                                match itemProps.Type' with
                                                | Some t ->
                                                    sb |> write $"\t\t{k1'}: {t} list{opt}\n"
                                                | None ->
                                                    match itemProps.AnyOf with
                                                    | Some aoo ->
                                                        let anyOfName = $"{name}{k1'}DU"
                                                        sb |> write $"\t\t{k1'}: {anyOfName} list{opt}\n"
                                                        anyOfs.Add(createAnyOf anyOfName aoo)
                                                    | None ->
                                                        failwith $"Error: unhandled property: %A{i}"
                                    | None ->
                                        sb |> write $"\t\t{k1'}: {t}{opt}\n"
                                | Some t when t = "object" ->
                                    sb |> write $"\t\t{k1'}: Map<string, string>{opt}\n"
                                | Some t ->
                                    match props.Description with
                                    | Some d when (String.IsNullOrWhiteSpace d |> not) && (d.Contains("Can be `")) ->
                                        ()
                                    | _ ->
                                        sb |> write $"\t\t{k1'}: {t}{opt}\n"
                                | _ ->
                                
                                    match props.Ref with
                                    | Some r ->
                                        sb |> write $"\t\t{k1'}: {r |> parseRef |> pascalCasify}{opt}\n"
                                    | None ->
                                        ()
                    )
            
                sb |> write "\t}\n"
                
                for e in enums do
                    sb |> write e
        
                for ao in anyOfs do
                    sb |> write ao

        sb.ToString().Replace("\t", "    ")

#if INTERACTIVE
    ;;
    open ModelBuilder;;
    let s = parseModel None;;
    System.IO.File.WriteAllText(__SOURCE_DIRECTORY__ + "/StripeModel.fs", s);;
#endif
