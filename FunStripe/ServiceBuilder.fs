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

module ServiceBuilder =

    let commentify (s: string) = 
        let s' = s.Replace("<p>", "").Replace("</p>", "").Replace("\n\n", "\n").Replace("\n", "\n\t\t///")
        $"<p>{s'}</p>"

    let pascalCasify (s: string) =
        Regex.Replace(s, @"(^|_|\.| |-)(\w)", fun (m: Match) -> m.Groups.[2].Value.ToUpper())

    let camelCasify (s: string) =
        Regex.Replace(s, @"( |_|-)(\w)", fun (m: Match) -> m.Groups.[2].Value.ToUpper())

    let escapeReservedName name =
        match name with
        | "end"
        | "open"
        | "type" ->
            $"``{name}``"
        | _ ->
            name

    let write s (sb: Text.StringBuilder) =
        sb.AppendLine s |> ignore

    let mapType (s: string) =
        match s with
        | "boolean" -> "bool"
        | "integer" -> "int"
        | "number" -> "decimal"
        | "array" -> "string list"
        | "object" -> "Map<string, string>"
        | _ -> s

    let formatParams (ss: string array) =
        (", ", 
            ss
            |> Array.map camelCasify
        ) |> String.Join

    let formatParametersString (parameters: JsonValue array) =
        (
            ", ",
            parameters
            |> Array.map (fun jv ->
                let name'' = jv.GetProperty("name").AsString()
                let required = jv.GetProperty("required").AsBoolean()
                let schema' = jv.GetProperty("schema")
                let type' =
                    schema'.TryGetProperty("type") |> function
                    | Some jv -> jv.AsString()
                    | None ->
                        schema'.TryGetProperty("anyOf") |> function
                        | Some jv -> jv.AsArray() |> Array.find (fun jv' -> jv'.GetProperty("type").AsString() <> "object") |> fun jv' -> jv'.GetProperty("type").AsString()
                        | None -> ""
                (name'', required, type')
            )
            |> Array.sortBy (fun (n, req, t) -> if req then 0 else 1)
            |> Array.map (fun (n, req, t) ->
                let opt = if req then "" else "?"
                $"{opt}{n |> escapeReservedName |> camelCasify}: {t |> mapType}"
            )
        ) |> String.Join

    let formatPathParams (path: string) =
        Regex.Replace(path, @"\{([^}]+)\}", fun m -> $"{{{m.Groups.[1].Value |> escapeReservedName |> camelCasify}}}")

    let singularise (s: string) =
        Regex.Replace(s, "s$", "")

    let parseRef (s: string) =
        let m = Regex.Match(s, "/([^/]+)$")
        if m.Success then
            m.Groups.[1].Value
        else
            failwith $"Unparsable reference: {s}"

    let parseService filePath =

        let root = __SOURCE_DIRECTORY__

        let filePath' = defaultArg filePath $@"{root}/res/spec3.sdk.json"
        let json = File.ReadAllText(filePath')

        let root = JsonValue.Parse json
        let components = root.Item "components"
        let schemas = components.Item "schemas"
        let operationPaths = root.Item "paths"

        let servicePaths =
            schemas.Properties
            |> Array.filter(fun (k, v) ->
                v.Properties
                |> Array.exists(fun (k, _) -> k = "x-stripeOperations")
            )
            |> Array.map(fun (k, v) ->
                (
                    k,
                    v.Properties
                    |> Array.filter(fun (k, _) -> k = "x-stripeOperations")
                    |> Array.collect(fun (_, v) -> v.AsArray())
                    |> Array.filter(fun jv -> jv.TryGetProperty("method_on") |> function | Some p -> p.AsString() = "service" | _ -> false)
                    |> Array.map(fun jv -> (jv.GetProperty("method_name").AsString(), jv.GetProperty("operation").AsString(), jv.GetProperty("path").AsString()))
                )
            )

        let sb = Text.StringBuilder()

        let mutable isFirstOccurrence = true

        sb |> write "namespace FunStripe\n\nopen FSharp.Json\n\nopen StripeModel\n\nmodule StripeService =\n"

        servicePaths
        |> Array.iter (fun (name, methodOperationPaths) ->

            let name' = name |> pascalCasify

            let keyword =
                if isFirstOccurrence then
                    isFirstOccurrence <- false
                    "type"
                else
                    "and"

            sb |> write $"\t{keyword} {name'}Service(?apiKey: string) = \n"
            sb |> write "\t\tmember _.RestApiClient = RestApi.RestApiClient(?apiKey = apiKey)\n"

            methodOperationPaths
            |> Array.iter (fun (method, operation, path) ->

                let pathRoot = Regex.Match(path, @"^/v[^/]+/([^/]+)/").Groups.[1].Value |> pascalCasify |> singularise
                let method' = if name'.StartsWith pathRoot then method else $"{method}For{pathRoot}"

                operationPaths.Properties
                |> Array.find (fun (p, _) -> p = path)
                |> fun (_, v) -> v.Properties
                |> Array.filter (fun (verb, _) -> verb = operation)
                |> Array.iter (fun (verb, v) ->

                    //get request method
                    let description = v.GetProperty("description").AsString()
                    let parameters = v.TryGetProperty("parameters") |> function | Some jv -> jv.AsArray() | None -> [||]
                    let parametersString = parameters |> formatParametersString
                    sb |> write $"\t\t///{description |> commentify}"
                    sb |> write $"\t\tmember this.{method' |> pascalCasify} ({parametersString}) ="
                    sb |> write $"\t\t\t$\"{path |> formatPathParams}\""

                    //get form values
                    let form = v.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/x-www-form-urlencoded") |> function | Some jv -> jv | None -> JsonValue.Null
                    let schema = form.TryGetProperty("schema") |> function | Some jv -> jv | None -> JsonValue.Null
                    let formParameters = schema.TryGetProperty("properties") |> function | Some jv -> jv.Properties | None -> [||]
    
                    // if formParameters.Any() then
                    //     //let requiredFormParameters = schema.TryGetProperty("required") |> function | Some jv -> jv.Properties | None -> [||]
                    //     let formParams = [
                    //         let rec getFormParams fpp =
                    //             for fp in fpp do
                    //                 yield! getFormParams fp
                    //         getFormParams formParameters
                    //     ]
                    //     ()

                    //get response type
                    let responseSchema = v.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
                    let responseType =
                        match responseSchema.TryGetProperty("$ref") with
                        | Some jv ->
                            jv.AsString()
                        | None ->
                            match responseSchema.TryGetProperty("anyOf") with
                            | Some jv when jv.AsArray() |> Array.isEmpty |> not ->
                                (jv.AsArray() |> Array.head).GetProperty("$ref").AsString()
                            | _ ->
                                match responseSchema.TryGetProperty("properties") with
                                | Some jv ->
                                    jv.GetProperty("data").GetProperty("items").GetProperty("$ref").AsString()
                                | _ ->
                                    failwith $"Unhandled response type: {name'} %A{responseSchema}"
                        |> parseRef |> pascalCasify

                    //set API method
                    match verb with
                    | "get" when formParameters.Any() ->
                        sb |> write $"\t\t\t|> this.RestApiClient.GetWithAsync<_, {responseType}> ``params``\n"
                    | "get" ->
                        sb |> write $"\t\t\t|> this.RestApiClient.GetAsync<{responseType}>\n"
                    | "post" when formParameters.Any() |> not ->
                        sb |> write $"\t\t\t|> this.RestApiClient.PostWithoutAsync<{responseType}>\n"
                    | "post" ->
                        sb |> write $"\t\t\t|> this.RestApiClient.PostAsync<_, {responseType}> ``params``\n"
                    | "delete" ->
                        sb |> write $"\t\t\t|> this.RestApiClient.DeleteAsync<{responseType}>\n"
                    | _ ->
                        failwith $"Unhandled verb: {verb}"

                )
            )
        )
            
        sb.ToString().Replace("\t", "    ")

#if INTERACTIVE
    ;;
    open ServiceBuilder;;
    let s = parseService None;;
    System.IO.File.WriteAllText(__SOURCE_DIRECTORY__ + "/StripeService.fs", s);;
#endif
