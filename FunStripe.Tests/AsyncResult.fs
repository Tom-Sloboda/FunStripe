namespace FunStripe
///Defines an ```AsyncResult``` as a ```Result``` wrapped in an ```Async```
type AsyncResult<'ok,'error> = Async<Result<'ok,'error>>

[<AutoOpen>]
module AsyncResultCE =
    ///Builds the ```AsyncResult``` computation expression
    type AsyncResultBuilder() = 

        member _.Return(x) : AsyncResult<_, _> =
            x
            |> Result.Ok
            |> async.Return

        member _.ReturnFrom(x: AsyncResult<_, _>) =
            x

        member _.Bind(x: AsyncResult<_, _>, f: 'a -> AsyncResult<'b, 'c>) : AsyncResult<_, _> =
            async {
                let! xResult = x
                match xResult with
                | Ok x ->
                    return! f x
                | Error e ->
                    return (Error e)
            }

        member this.Zero() =
            this.Return ()

    ///Instantiates the ```AsyncResult``` computation expression
    let asyncResult = AsyncResultBuilder()
    